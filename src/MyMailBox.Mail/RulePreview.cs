using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailSearchService
{
    private static string PreviewDocuments => Documents.Replace("JOIN search_meta","JOIN preview_meta",StringComparison.Ordinal);

    internal Task<RulePreview> Preview(ReceiveRule rule,SearchLibrary library,CancellationToken token) => Task.Run(() =>
    {
        using var db=_store.Open(readOnly:true);using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TEMP TABLE search_meta AS SELECT * FROM main.search_meta LIMIT 0;
            CREATE UNIQUE INDEX temp.preview_meta_key ON search_meta(id);
            CREATE TEMP VIEW preview_meta AS SELECT * FROM temp.search_meta UNION ALL SELECT x.* FROM main.search_meta x WHERE NOT EXISTS(SELECT 1 FROM temp.search_meta t WHERE t.id=x.id);
            CREATE TEMP TABLE derived_search_value(field TEXT,id TEXT,source TEXT,value TEXT,PRIMARY KEY(field,id));
            """;
        cmd.ExecuteNonQuery(); _metadata.Prepare(token,db);
        var unavailable=new HashSet<string>();
        var include=Compile(db,cmd,ConditionTree.Parse(rule.Condition),unavailable,token,preview:true);
        var exclude=string.IsNullOrWhiteSpace(rule.Exception) ? "0" : Compile(db,cmd,ConditionTree.Parse(rule.Exception),unavailable,token,preview:true,seed:10000);
        cmd.Parameters.AddWithValue("$a",(object?)rule.Account?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f",(object?)rule.Folder?.ToString() ?? DBNull.Value);
        string query=PreviewDocuments + $", tested AS (SELECT subject,({include}) AS inc,({exclude}) AS exc FROM docs WHERE ($a IS NULL OR accountid=$a) AND ($f IS NULL OR folderid=$f))";
        string classify=unavailable.Count>0 ? "'unknown'" : "CASE WHEN inc AND exc THEN 'exception' WHEN inc AND NOT exc THEN 'match' WHEN (inc AND NOT exc) IS NULL THEN 'unknown' ELSE 'no-match' END";
        query+=", results AS (SELECT subject," + classify + " AS reason FROM tested) ";
        cmd.CommandText=query+"SELECT reason,count(*) FROM results GROUP BY reason";
        var counts=new Dictionary<string,int>();using(var r=cmd.ExecuteReader())while(r.Read()){token.ThrowIfCancellationRequested();counts[r.GetString(0)]=r.GetInt32(1);}
        var samples=new List<string>();cmd.CommandText=query+"SELECT subject,reason FROM results ORDER BY CASE reason WHEN 'match' THEN 0 WHEN 'exception' THEN 1 WHEN 'unknown' THEN 2 ELSE 3 END,subject LIMIT 16";
        using(var r=cmd.ExecuteReader())while(r.Read())samples.Add((r.GetString(1) switch {"match"=>"命中","exception"=>"被例外排除","unknown"=>"条件未知",_=>"未命中"})+" · "+r.GetString(0));
        var scope=library.Scopes.FirstOrDefault(s=>s.Account==rule.Account && s.Folder==rule.Folder);
        var warnings=unavailable.Select(x=>"缺少扩展能力 "+x+"，执行会暂停。").ToList();
        if(scope is null)warnings.Add("原范围已不可用。");
        var effects=rule.Actions.Select(a=>a.Kind switch{RuleActionKind.Move=>"移动并移出源目录 → "+(library.Scopes.FirstOrDefault(s=>s.Folder==a.Destination)?.Name ?? "目标缺失"),
            RuleActionKind.Label=>"添加标签 "+a.Label,RuleActionKind.Star=>"加星标",RuleActionKind.Unstar=>"取消星标",RuleActionKind.Read=>"设为已读",_=>"设为未读"}).ToArray();
        return new RulePreview(scope?.Name ?? "已失效的范围",counts.Values.Sum(),counts.GetValueOrDefault("match"),counts.GetValueOrDefault("unknown"),counts.GetValueOrDefault("exception"),samples,effects,warnings);
    },token);

    internal string? Unavailable(ConditionTree tree,Guid account)
    {
        var extensions=_extensions();
        string? Visit(ConditionNode node)
        {
            if(node.Criterion is { } c && c.Field.StartsWith("plugin.",StringComparison.Ordinal))
            {
                var ext=extensions.SingleOrDefault(e=>e.Id==c.Field && e.Operator==c.Operator);
                if(ext is null) return "缺少字段或运算符 "+c.Field+"。";
                if(!ext.CanRead(account)) return "扩展的账户读取授权已撤销："+c.Field+"。";
            }
            foreach(var child in node.Children)if(Visit(child) is {} reason)return reason;
            return null;
        }
        return Visit(tree.Root);
    }
}

public sealed partial class ReceiveRuleService
{
    public async Task<RulePreview> Preview(ReceiveRule rule,CancellationToken token=default) =>
        await search.Preview(rule,await search.Library(token),token);

    private string? Access(Work work,ConditionTree tree)
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT provider,capabilities FROM accounts WHERE id=$a AND enabled AND NOT removed";
        c.Parameters.AddWithValue("$a",work.Account.ToString());
        using(var r=c.ExecuteReader())
        {
            if(!r.Read()) return "账户已停用或移除。";
            var caps=(AccountCapabilities)r.GetInt32(1);string provider=r.GetString(0);
            if(provider=="local" ? !caps.HasFlag(AccountCapabilities.Folders) : provider is not ("imap" or "imap-smtp" or "pop3") || !caps.HasFlag(AccountCapabilities.Receive))
                return "账户的收件或整理能力已变更。";
        }
        c.CommandText="SELECT count(*) FROM folders WHERE account_id=$a AND id=$folder";
        var folder=c.Parameters.AddWithValue("$folder",work.Folder.ToString());
        foreach(var id in work.Rule.Actions.Where(a=>a.Destination.HasValue).Select(a=>a.Destination!.Value).Prepend(work.Folder))
        {folder.Value=id.ToString();if((long)c.ExecuteScalar()! == 0)return "来源或动作目标目录已移除。";}
        return search.Unavailable(tree,work.Account);
    }

    public Task Retry(string run,CancellationToken token=default) => Task.Run(() =>
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="""
            UPDATE rule_run SET state='pending',detail='请求重新判断' WHERE id=$id AND state IN ('paused','unknown','failed')
                AND NOT EXISTS(SELECT 1 FROM rule_action_log WHERE run=$id AND state<>'done')
                AND EXISTS(SELECT 1 FROM receive_rule d WHERE d.id=rule_run.rule AND d.version=rule_run.version AND d.enabled)
            """;
        c.Parameters.AddWithValue("$id",run);if(c.ExecuteNonQuery()!=1)throw new InvalidDataException("原规则已变更、记录未暂停或有待核对的动作，无法自动重试。");
    },token);

    public async Task Undo(Guid operation,CancellationToken token=default)
    {
        using var db=store.Open();using var c=db.CreateCommand();
        c.CommandText="SELECT undo FROM rule_action_log WHERE op=$op AND state='done'";c.Parameters.AddWithValue("$op",operation.ToString());
        if(c.ExecuteScalar() is not string id)throw new InvalidDataException("此动作没有可用的撤销记录。");
        await mailActions.Undo(Guid.Parse(id),token);c.CommandText="UPDATE rule_action_log SET state='undone',detail='已撤销本地移动',undo=NULL WHERE op=$op";c.ExecuteNonQuery();
    }
}
