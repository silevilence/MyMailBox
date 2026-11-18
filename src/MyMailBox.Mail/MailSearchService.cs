using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Globalization;

namespace MyMailBox.Mail;

public sealed partial class MailSearchService : IMailSearch
{
    private readonly SqliteMailStore _store;
    private readonly ProtocolMailService _protocols;
    private readonly Func<IReadOnlyList<SearchExtension>> _extensions;
    private readonly BodySearchIndex _bodyIndex;
    private readonly SearchMetadata _metadata;
    public MailSearchService(SqliteMailStore store, ProtocolMailService protocols, Func<IReadOnlyList<SearchExtension>>? extensions = null)
    {
        _extensions = extensions ?? (() => []);
        _store = store; _protocols = protocols; _metadata = new(store);
        SearchMetadata.Initialize(store); BodySearchIndex.Initialize(store); _bodyIndex = new(store); InitializeLibrary();
    }

    public Task RebuildBodyIndex(IProgress<SearchIndexProgress>? progress = null, CancellationToken token = default) => Task.Run(() =>
    {
        _metadata.Prepare(token); _bodyIndex.Prepare(true, progress, token);
    }, token);

    private static SearchField TextField(string id, string label) => new(id, label, SearchKind.Text, [SearchMatch.Contains, SearchMatch.Equals]);
    private static SearchField DateField(string id, string label) => new(id, label, SearchKind.LocalDate, [SearchMatch.Equals, SearchMatch.OnOrAfter, SearchMatch.Before]);
    public IReadOnlyList<SearchField> Fields => BuiltinFields.Concat(_extensions().Select(e => new SearchField(e.Id,e.Name,SearchKind.Text,[SearchMatch.Extension]) { Operator=e.Operator, OperatorName=e.OperatorName })).ToArray();
    private static readonly SearchField[] BuiltinFields =
    [
        TextField("subject","主题"),TextField("body","已下载正文"),TextField("from","发件人 From"),TextField("sender","实际发送者 Sender"),TextField("reply","回复地址 Reply-To"),
        TextField("to","收件人 To"),TextField("cc","抄送 Cc"),TextField("bcc","可见密送 Bcc"),TextField("account","账户名称"),TextField("folder","目录路径"),
        TextField("attachment","附件名"),TextField("header","原文邮件头"),TextField("priority","优先级"),TextField("thread","会话标识"),
        TextField("tag","本地标签"),DateField("sent","发送时间（Date）"),DateField("received","接收时间（服务器或 Received）"),
        new("size","原文字节数",SearchKind.Number,[SearchMatch.AtLeast,SearchMatch.AtMost,SearchMatch.Equals]),
        new("read","已读",SearchKind.Boolean,[SearchMatch.Equals]),new("star","星标",SearchKind.Boolean,[SearchMatch.Equals]),
        TextField("accountid","稳定账户 ID"),TextField("folderid","稳定目录 ID")
    ];

    // Keep one searchable row per message. A downloaded IMAP body replaces its summary-only hit.
    private static readonly string Documents=BuildDocuments(false);
    private static readonly string RecentDocuments=BuildDocuments(true);
    private static readonly string PreviewDocuments = BuildDocuments(false, preview: true);

    private static string BuildDocuments(bool recent, bool preview = false)
    {
        // Preview-only values live on this connection. Indexed joins avoid materializing
        // every persisted message body when an automation task inspects one folder.
        string Meta(string field) => preview
            ? $"CASE WHEN y.id IS NULL THEN x.{field} ELSE y.{field} END"
            : $"x.{field}";

        return $"""
        WITH docs AS (
          SELECT coalesce(s.folder || '/' || s.uid,m.id) AS id,a.id AS accountid,f.id AS folderid,a.name AS account,f.path AS folder,m.subject,
            NULLIF(m.sender,'') AS "from",{Meta("actual_sender")} AS sender,{Meta("reply_to")} AS reply,NULLIF(m.recipient,'') AS "to",{Meta("cc")} AS cc,{Meta("bcc")} AS bcc,
            {Meta("files")} AS attachment,{Meta("headers")} AS header,{Meta("priority")} AS priority,{Meta("thread")} AS thread,{Meta("sent")} AS sent,
            coalesce(json_extract(s.envelope,'$.Received'),{Meta("received")}) AS received,m.size,m.is_read AS read,
            coalesce((s.flags & {(int)MailKit.MessageFlags.Flagged})<>0,t.starred,0) AS star,coalesce(rt.labels,t.labels) AS tag,
            m.id AS localid,s.uid,fm.validity,fm.path AS remote_path,coalesce({Meta("complete")},0) AS complete,{Meta("body")} AS body
          FROM {(recent?"search_meta x INDEXED BY search_meta_dates CROSS JOIN messages m ON m.id=x.id AND x.hash=m.sha256":"messages m")}
          JOIN folders f ON f.id=m.folder_id JOIN accounts a ON a.id=f.account_id
          {(recent?"":"LEFT JOIN main.search_meta x ON x.id=m.id AND x.hash=m.sha256")}
          {(preview ? "LEFT JOIN temp.search_meta y ON y.id=m.id AND y.hash=m.sha256" : "")}
          LEFT JOIN imap_summaries s ON s.message=m.id LEFT JOIN imap_folders fm ON fm.id=s.folder
          LEFT JOIN mail_marks t ON t.folder=f.id AND t.item=m.id LEFT JOIN mail_marks rt ON rt.folder=s.folder AND rt.item=CAST(s.uid AS TEXT)
          WHERE a.enabled AND NOT a.removed {(recent?"AND x.sent >= $cut":"")}
          UNION ALL
          SELECT s.folder || '/' || s.uid,a.id,f.id,a.name,f.path,s.subject,NULLIF(s.sender,''),
            NULLIF(json_extract(s.envelope,'$.Sender'),''),NULLIF(json_extract(s.envelope,'$.Reply'),''),
            NULLIF(json_extract(s.envelope,'$.To'),''),NULLIF(json_extract(s.envelope,'$.Cc'),''),NULLIF(json_extract(s.envelope,'$.Bcc'),''),
            NULL,NULL,NULL,json_extract(s.envelope,'$.Thread'),NULLIF(s.date_ms,0),json_extract(s.envelope,'$.Received'),
            s.size,(s.flags & {(int)MailKit.MessageFlags.Seen})<>0,(s.flags & {(int)MailKit.MessageFlags.Flagged})<>0,t.labels,NULL,s.uid,fm.validity,fm.path,0,NULL
          FROM imap_summaries s JOIN imap_folders fm ON s.folder=fm.id JOIN folders f ON f.id=s.folder
          JOIN accounts a ON a.id=f.account_id LEFT JOIN mail_marks t ON t.folder=f.id AND t.item=CAST(s.uid AS TEXT)
          WHERE a.enabled AND NOT a.removed AND s.message IS NULL AND NOT s.missing {(recent?"AND s.date_ms >= $cut":"")}
        )
        """;
    }

    public async Task<SearchPage> Search(SearchRequest request, CancellationToken token = default)
    {
        try { return await Task.Run(() => SearchLocal(request, token), token); }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    private SearchPage SearchLocal(SearchRequest request, CancellationToken token)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(request));
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        db.CreateFunction<string?, string?, long?>("mb_contains", (s, v) => s is null || v is null ? null : s.Contains(v, StringComparison.OrdinalIgnoreCase) ? 1 : 0, true);
        db.CreateCollation("MB_ORDINAL", StringComparer.OrdinalIgnoreCase.Compare);
        var filters = request.Criteria.Select((c, i) => Filter(cmd, c, i)).ToArray();
        _metadata.Prepare(token); _bodyIndex.Prepare(false, null, token);
        var highlightsSql = new List<string>();
        var unavailable = new HashSet<string>();
        string? condition = request.Condition is null ? null : Compile(db, cmd, request.Condition, unavailable, token, highlights:highlightsSql);
        string combined = string.Join(" AND ", filters.Concat(condition is null ? [] : new[] { condition }));
        if (request.RemoteBatch is Guid batch && combined.Length > 0 && unavailable.Count == 0)
        {
            cmd.Parameters.AddWithValue("$remote", batch.ToString());
            combined = $"(({combined}) OR id IN(SELECT id FROM remote_search_hit WHERE batch=$remote))";
        }
        var order = Ordering(request.Order);
        using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        token.ThrowIfCancellationRequested();
        using var tx = db.BeginTransaction(deferred: true); cmd.Transaction = tx;
        var (full,missing,indexed,noBody,emptySubject)=LocalCoverage(cmd);
        int summaries,unknown=0;
        cmd.CommandText="""
            SELECT count(*),count(CASE WHEN s.subject='' THEN 1 END) FROM imap_summaries s JOIN imap_folders i ON i.id=s.folder
            JOIN folders f ON f.id=i.id JOIN accounts a ON a.id=f.account_id WHERE a.enabled AND NOT a.removed AND s.message IS NULL AND NOT s.missing
            """;
        using(var r=cmd.ExecuteReader()){r.Read();summaries=r.GetInt32(0);emptySubject+=r.GetInt32(1);}
        if(condition is not null)
        {
            var leaf=request.Criteria.Length==0 && request.RemoteBatch is null?request.Condition?.Root.Criterion:null;
            if(leaf is {Field:"body",Match:SearchMatch.Contains})unknown=noBody+summaries;
            else if(leaf is {Field:"subject",Match:SearchMatch.Contains})unknown=emptySubject;
            else{cmd.CommandText=Documents+" SELECT count(*) FROM docs WHERE ("+combined+") IS NULL";unknown=Convert.ToInt32(cmd.ExecuteScalar());}
        }
        string where = combined.Length == 0 ? "" : " WHERE " + combined;
        var highlights = HighlightTerms(request).ToArray();
        var projection = " SELECT id,accountid,folderid,folder,subject,\"from\",sent,size,localid,uid,validity," + (highlights.Length > 0 ? "body" : "NULL") + "," + (highlightsSql.Count == 0 ? "NULL" : "coalesce(" + string.Join(",",highlightsSql) + ",NULL)") + " FROM docs";
        string sort=" ORDER BY "+order+",id LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", request.Limit + 1); cmd.Parameters.AddWithValue("$offset", request.Offset);
        var hits = new List<SearchHit>();
        void ReadHits(string docs,string predicate)
        {
            cmd.CommandText=docs+projection+predicate+sort;
            using var r=cmd.ExecuteReader();while(r.Read())
        {
            token.ThrowIfCancellationRequested();
            hits.Add(new(r.GetString(0), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? "" : r.GetString(5), r.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)), r.GetInt64(7),
                r.IsDBNull(8) ? null : Guid.Parse(r.GetString(8)), r.IsDBNull(9) ? null : (uint)r.GetInt64(9), r.IsDBNull(10) ? null : (uint)r.GetInt64(10)) { Excerpt = (!r.IsDBNull(11) ? Excerpt(r.GetString(11), highlights) : null) ?? (r.IsDBNull(12) ? null : System.Text.Json.JsonSerializer.Deserialize<SearchExcerpt>(r.GetString(12))) });
        }
        }
        // A full page above this date boundary is also the exact globally ordered page.
        // Include ties and remote summaries in the same ordering, then fall back for sparse matches.
        if(request.Order==SearchOrder.Newest && request.Offset<=200 && full>1024)
        {
            cmd.CommandText="SELECT sent FROM search_meta WHERE sent IS NOT NULL ORDER BY sent DESC LIMIT 1 OFFSET 511";
            if(cmd.ExecuteScalar() is long cut)
            {
                cmd.Parameters.AddWithValue("$cut",cut);string recentWhere=where;
                var single=request.RemoteBatch is null?(request.Criteria.Length==0?request.Condition?.Root.Criterion:
                    request.Criteria.Length==1&&request.Condition is null?request.Criteria[0]:null):null;
                if(single is {Field:"body",Match:SearchMatch.Contains} && single.Value.Length>0)
                {
                    cmd.Parameters.AddWithValue("$fastText",request.Condition is null?single.Value.Trim():single.Value);
                    recentWhere=" WHERE mb_contains(body,$fastText)";
                }
                ReadHits(RecentDocuments,recentWhere);
            }
        }
        if(hits.Count<=request.Limit){hits.Clear();ReadHits(Documents,where);}
        return new SearchPage(hits.Take(request.Limit).ToArray(), hits.Count > request.Limit, full, summaries, missing) { IndexedBodies = indexed, Unknown = unknown, UnavailableFields = unavailable.ToArray() };
    }

    private readonly object _coverageLock=new();
    private long _coverageVersion=-1;
    private (int Full,int Missing,int Indexed,int NoBody,int EmptySubjects) _coverage;
    private (int,int,int,int,int) LocalCoverage(SqliteCommand cmd)
    {
        cmd.CommandText="SELECT revision FROM search_coverage_revision";long revision=(long)cmd.ExecuteScalar()!;
        lock(_coverageLock){if(_coverageVersion==revision)return _coverage;}
        cmd.CommandText="""
            SELECT count(*),coalesce(sum(coalesce(x.complete,0)=0),0),count(CASE WHEN x.body IS NOT NULL AND b.message IS NOT NULL THEN 1 END),
                count(CASE WHEN x.body IS NOT NULL THEN NULL ELSE 1 END),count(CASE WHEN m.subject='' THEN 1 END)
            FROM messages m INDEXED BY search_message_coverage JOIN folders f ON f.id=m.folder_id JOIN accounts a ON a.id=f.account_id
            LEFT JOIN search_meta x INDEXED BY search_meta_coverage ON x.id=m.id AND x.hash=m.sha256 LEFT JOIN body_index_state b ON b.message=m.id
            WHERE a.enabled AND NOT a.removed
            """;
        int full,missing,indexed,noBody,emptySubject;
        using(var r=cmd.ExecuteReader()){r.Read();full=r.GetInt32(0);missing=r.GetInt32(1);indexed=r.GetInt32(2);noBody=r.GetInt32(3);emptySubject=r.GetInt32(4);}
        var result=(full,missing,indexed,noBody,emptySubject);
        lock(_coverageLock){_coverage=result;_coverageVersion=revision;}
        return result;
    }

    private static string Ordering(SearchOrder order) => order switch
    {
        SearchOrder.Newest => "sent DESC",
        SearchOrder.Oldest => "sent ASC",
        SearchOrder.Largest => "size DESC",
        SearchOrder.Subject => "subject COLLATE MB_ORDINAL",
        _ => throw new InvalidDataException("未知排序方式。")
    };

    public Task<int> Position(SearchHit hit, SearchOrder order, CancellationToken token = default) => Task.Run(() =>
    {
        using var db = _store.Open(); db.CreateCollation("MB_ORDINAL", StringComparer.OrdinalIgnoreCase.Compare);
        using var cmd = db.CreateCommand();
        cmd.CommandText = Documents + ", ranked AS (SELECT id,row_number() OVER (ORDER BY " + Ordering(order) + ",id)-1 AS pos FROM docs WHERE folderid=$folder AND accountid=$account) SELECT pos FROM ranked WHERE id=$id";
        cmd.Parameters.AddWithValue("$folder", hit.Folder.ToString()); cmd.Parameters.AddWithValue("$account", hit.Account.ToString());
        cmd.Parameters.AddWithValue("$id", hit.Id);
        return cmd.ExecuteScalar() is long value ? checked((int)value) : throw new InvalidDataException("邮件已移出原目录，请刷新结果。");
    }, token);

    private string Filter(SqliteCommand cmd, FieldCriterion criterion, int index)
    {
        var field = BuiltinFields.SingleOrDefault(f => f.Id == criterion.Field) ?? throw new InvalidDataException("未知搜索字段。");
        if (!field.Matches.Contains(criterion.Match)) throw new InvalidDataException("字段不支持此条件。");
        var column = "\"" + field.Id + "\"";
        string parameter = "$f" + index;
        string value = criterion.Value.Trim();
        if (value.Length == 0) throw new InvalidDataException("请填写条件值。");
        if (field.Kind == SearchKind.LocalDate)
        {
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date == DateOnly.MaxValue)
                throw new InvalidDataException("日期请使用 yyyy-MM-dd。");
            long Midnight(DateOnly d) => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)).ToUnixTimeMilliseconds();
            cmd.Parameters.AddWithValue(parameter, Midnight(date));
            if (criterion.Match == SearchMatch.Before) return column + " < " + parameter;
            if (criterion.Match == SearchMatch.OnOrAfter) return column + " >= " + parameter;
            cmd.Parameters.AddWithValue(parameter + "end", Midnight(date.AddDays(1)));
            return $"({column} >= {parameter} AND {column} < {parameter}end)";
        }
        if (field.Kind == SearchKind.Boolean)
        {
            int flag = value.ToLowerInvariant() switch { "true" or "1" or "是" => 1, "false" or "0" or "否" => 0, _ => throw new InvalidDataException("状态请使用 是／否。") };
            cmd.Parameters.AddWithValue(parameter, flag); return column + " = " + parameter;
        }
        if (field.Kind == SearchKind.Number)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size)) throw new InvalidDataException("大小请使用非负整数字节。");
            cmd.Parameters.AddWithValue(parameter, size);
            return column + (criterion.Match switch { SearchMatch.AtLeast => " >= ", SearchMatch.AtMost => " <= ", _ => " = " }) + parameter;
        }
        cmd.Parameters.AddWithValue(parameter, value);
        string Compare(string expression) => criterion.Match == SearchMatch.Contains ? $"mb_contains({expression},{parameter})" : $"{expression} = {parameter} COLLATE MB_ORDINAL";
        if (field.Id == "body" && criterion.Match == SearchMatch.Contains) return BodySearchIndex.Contains(cmd, Compare(column), value, parameter + "grams");
        return field.Id == "tag" ? $"EXISTS (SELECT 1 FROM json_each(tag) WHERE {Compare("value")})" : Compare(column);
    }

    public async Task<string> Read(SearchHit hit, CancellationToken token = default)
    {
        using var db = _store.Open(); using var c = db.CreateCommand();
        c.CommandText = Documents + " SELECT localid,remote_path,uid,validity FROM docs WHERE id=$id AND accountid=$account AND folderid=$folder";
        c.Parameters.AddWithValue("$id", hit.Id); c.Parameters.AddWithValue("$account", hit.Account.ToString()); c.Parameters.AddWithValue("$folder", hit.Folder.ToString());
        string? local, path; uint uid, validity;
        using (var r = c.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidDataException("邮件已移动或来源不可用，请刷新搜索结果。");
            local = r.IsDBNull(0) ? null : r.GetString(0); path = r.IsDBNull(1) ? null : r.GetString(1);
            uid = r.IsDBNull(2) ? 0 : (uint)r.GetInt64(2); validity = r.IsDBNull(3) ? 0 : (uint)r.GetInt64(3);
        }
        if (local is null)
            return (await _protocols.OpenMessage(hit.Account, path!, new(hit.Folder, validity, uid, hit.Subject, hit.From, hit.Size, null, false), true, token)).Text;
        var mail = await new MimeReader().Parse(await _store.Read(Guid.Parse(local), token), token);
        return string.IsNullOrEmpty(mail.Text) ? "此邮件没有纯文本正文，可在本地邮箱查看安全 HTML 或附件。" : mail.Text;
    }
}
