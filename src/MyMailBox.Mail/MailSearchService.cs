using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Globalization;

namespace MyMailBox.Mail;

public sealed partial class MailSearchService : IMailSearch
{
    private readonly SqliteMailStore _store;
    private readonly ProtocolMailService _protocols;
    private readonly BodySearchIndex _bodyIndex;
    private readonly SearchMetadata _metadata;
    public MailSearchService(SqliteMailStore store, ProtocolMailService protocols)
    {
        _store = store; _protocols = protocols; _metadata = new(store);
        SearchMetadata.Initialize(store); BodySearchIndex.Initialize(store); _bodyIndex = new(store);
    }

    public Task RebuildBodyIndex(IProgress<SearchIndexProgress>? progress = null, CancellationToken token = default) => Task.Run(() =>
    {
        _metadata.Prepare(token); _bodyIndex.Prepare(true, progress, token);
    }, token);

    private static SearchField TextField(string id, string label) => new(id, label, SearchKind.Text, [SearchMatch.Contains, SearchMatch.Equals]);
    private static SearchField DateField(string id, string label) => new(id, label, SearchKind.LocalDate, [SearchMatch.Equals, SearchMatch.OnOrAfter, SearchMatch.Before]);
    public IReadOnlyList<SearchField> Fields { get; } =
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
    private static readonly string Documents = $"""
        WITH docs AS (
          SELECT coalesce(s.folder || '/' || s.uid,m.id) AS id,a.id AS accountid,f.id AS folderid,a.name AS account,f.path AS folder,m.subject,
            NULLIF(m.sender,'') AS "from",x.actual_sender AS sender,x.reply_to AS reply,NULLIF(m.recipient,'') AS "to",x.cc,x.bcc,
            x.files AS attachment,x.headers AS header,x.priority,x.thread,x.sent,
            coalesce(json_extract(s.envelope,'$.Received'),x.received) AS received,m.size,m.is_read AS read,
            coalesce((s.flags & {(int)MailKit.MessageFlags.Flagged})<>0,t.starred,0) AS star,coalesce(rt.labels,t.labels) AS tag,
            m.id AS localid,s.uid,fm.validity,fm.path AS remote_path,coalesce(x.complete,0) AS complete,x.body
          FROM messages m JOIN folders f ON f.id=m.folder_id JOIN accounts a ON a.id=f.account_id
          LEFT JOIN search_meta x ON x.id=m.id AND x.hash=m.sha256
          LEFT JOIN imap_summaries s ON s.message=m.id LEFT JOIN imap_folders fm ON fm.id=s.folder
          LEFT JOIN mail_marks t ON t.folder=f.id AND t.item=m.id LEFT JOIN mail_marks rt ON rt.folder=s.folder AND rt.item=CAST(s.uid AS TEXT)
          WHERE a.enabled AND NOT a.removed
          UNION ALL
          SELECT s.folder || '/' || s.uid,a.id,f.id,a.name,f.path,s.subject,NULLIF(s.sender,''),
            NULLIF(json_extract(s.envelope,'$.Sender'),''),NULLIF(json_extract(s.envelope,'$.Reply'),''),
            NULLIF(json_extract(s.envelope,'$.To'),''),NULLIF(json_extract(s.envelope,'$.Cc'),''),NULLIF(json_extract(s.envelope,'$.Bcc'),''),
            NULL,NULL,NULL,json_extract(s.envelope,'$.Thread'),NULLIF(s.date_ms,0),json_extract(s.envelope,'$.Received'),
            s.size,(s.flags & {(int)MailKit.MessageFlags.Seen})<>0,(s.flags & {(int)MailKit.MessageFlags.Flagged})<>0,t.labels,NULL,s.uid,fm.validity,fm.path,0,NULL
          FROM imap_summaries s JOIN imap_folders fm ON s.folder=fm.id JOIN folders f ON f.id=s.folder
          JOIN accounts a ON a.id=f.account_id LEFT JOIN mail_marks t ON t.folder=f.id AND t.item=CAST(s.uid AS TEXT)
          WHERE a.enabled AND NOT a.removed AND s.message IS NULL AND NOT s.missing
        )
        """;

    public Task<SearchPage> Search(SearchRequest request, CancellationToken token = default) => Task.Run(() =>
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(request));
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        db.CreateFunction<string?, string?, long?>("mb_contains", (s, v) => s is null || v is null ? null : s.Contains(v, StringComparison.OrdinalIgnoreCase) ? 1 : 0, true);
        db.CreateCollation("MB_ORDINAL", StringComparer.OrdinalIgnoreCase.Compare);
        var filters = request.Criteria.Select((c, i) => Filter(cmd, c, i)).ToArray();
        var unavailable = new HashSet<string>();
        string? condition = request.Condition is null ? null : Compile(db, cmd, request.Condition, unavailable, token);
        string combined = string.Join(" AND ", filters.Concat(condition is null ? [] : new[] { condition }));
        var order = Ordering(request.Order);
        _metadata.Prepare(token); _bodyIndex.Prepare(false, null, token);
        using var tx = db.BeginTransaction(deferred: true); cmd.Transaction = tx;
        cmd.CommandText = Documents + " SELECT count(localid),count(*)-count(localid),sum(localid IS NOT NULL AND NOT complete)," + (condition is null ? "0" : $"count(CASE WHEN ({combined}) IS NULL THEN 1 END)") + ",count(CASE WHEN body IS NOT NULL AND localid IN(SELECT message FROM body_index_state) THEN 1 END) FROM docs";
        int full, summaries, missing, unknown, indexed;
        using (var r = cmd.ExecuteReader())
        {
            r.Read(); full = r.GetInt32(0); summaries = r.GetInt32(1); missing = r.IsDBNull(2) ? 0 : r.GetInt32(2); unknown = r.GetInt32(3); indexed = r.GetInt32(4);
        }
        string where = combined.Length == 0 ? "" : " WHERE " + combined;
        var highlights = HighlightTerms(request).ToArray();
        cmd.CommandText = Documents + " SELECT id,accountid,folderid,folder,subject,\"from\",sent,size,localid,uid,validity," + (highlights.Length > 0 ? "body" : "NULL") + " FROM docs" + where +
            " ORDER BY " + order + ",id LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", request.Limit + 1); cmd.Parameters.AddWithValue("$offset", request.Offset);
        var hits = new List<SearchHit>();
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            hits.Add(new(r.GetString(0), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? "" : r.GetString(5), r.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)), r.GetInt64(7),
                r.IsDBNull(8) ? null : Guid.Parse(r.GetString(8)), r.IsDBNull(9) ? null : (uint)r.GetInt64(9), r.IsDBNull(10) ? null : (uint)r.GetInt64(10)) { Excerpt = r.IsDBNull(11) ? null : Excerpt(r.GetString(11), highlights) });
        }
        return new SearchPage(hits.Take(request.Limit).ToArray(), hits.Count > request.Limit, full, summaries, missing) { IndexedBodies = indexed, Unknown = unknown, UnavailableFields = unavailable.ToArray() };
    }, token);

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
        var field = Fields.SingleOrDefault(f => f.Id == criterion.Field) ?? throw new InvalidDataException("未知搜索字段。");
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
