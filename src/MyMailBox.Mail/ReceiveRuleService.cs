using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Text.Json;

namespace MyMailBox.Mail;

public sealed partial class ReceiveRuleService(SqliteMailStore store, MailSearchService search, IMailActionService mailActions, IMailOrganizer organizer) : IReceiveRules
{
    public ReceiveRuleService(SqliteMailStore store, MailSearchService search, ProtocolMailService protocols) : this(store,search,protocols,protocols) { }

    private static bool SendsMail(RuleAction a) => a.Kind is RuleActionKind.AutoReply or RuleActionKind.AutoForward;
    private ProtocolMailService Outbound => mailActions as ProtocolMailService ?? throw new InvalidDataException("此宿主未提供自动外发能力。");
    private static string Approval(ReceiveRule rule) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(rule with { Version = 0, Name = rule.Name.Trim(), MailApproval = null })));

    public async Task<RuleDashboard> Dashboard(CancellationToken token = default)
    {
        var scopes = (await search.Library(token)).Scopes;
        using var db = store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT definition FROM receive_rule ORDER BY priority,name,id";
        var rules = new List<ReceiveRule>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) rules.Add(JsonSerializer.Deserialize<ReceiveRule>(r.GetString(0))!);
        cmd.CommandText = "SELECT id,event,rule,version,name,state,detail FROM rule_run ORDER BY created DESC,rowid DESC LIMIT 100";
        var runs = new List<RuleRun>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) runs.Add(new(r.GetString(0), r.GetString(1), Guid.Parse(r.GetString(2)), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return new(rules, runs, scopes);
    }

    public async Task<ReceiveRule> Save(ReceiveRule rule, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Actions.Length == 0) throw new InvalidDataException("请填写规则名并添加至少一个动作。");
        var library = await search.Library(token);
        if (rule.Enabled && !library.Scopes.Any(s => s.Account == rule.Account && s.Folder == rule.Folder)) throw new InvalidDataException("规则范围已缺失，请修复后启用。");
        search.ValidateCondition(Conditions(rule));
        foreach (var a in rule.Actions)
        {
            if (SendsMail(a))
            {
                await Outbound.ValidateAutomatic(rule.Account,a.Kind==RuleActionKind.AutoForward,a.MailArgument ?? "",token);
                if (rule.Enabled && rule.MailApproval != Approval(rule))
                    throw new InvalidDataException("保存外发规则前请试运行并确认收件人和范围。");
            }
            if (!Enum.IsDefined(a.Kind) || a.Kind == RuleActionKind.Label && string.IsNullOrWhiteSpace(a.Label)) throw new InvalidDataException("动作或标签值无效。");
            if (a.Kind == RuleActionKind.Move && (a.Destination is null || !library.Scopes.Any(s => s.Folder == a.Destination && s.Account == rule.Account)))
                throw new InvalidDataException("移动需要明确的账户及同账户目标目录。");
        }
        if (!rule.Enabled || !rule.Actions.Any(SendsMail)) rule = rule with { MailApproval = null };
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = "SELECT version FROM receive_rule WHERE id=$id"; c.Parameters.AddWithValue("$id", rule.Id.ToString());
        int version = Convert.ToInt32(c.ExecuteScalar());
        if (version != rule.Version) throw new InvalidDataException("规则已被修改，请重新载入。");
        rule = rule with { Version = version + 1, Name = rule.Name.Trim() };
        c.CommandText = "INSERT OR REPLACE INTO receive_rule VALUES($id,$v,$name,$account,$folder,$order,$enabled,$history,$json)";
        c.Parameters.AddWithValue("$v", rule.Version); c.Parameters.AddWithValue("$name", rule.Name); c.Parameters.AddWithValue("$account", (object?)rule.Account?.ToString() ?? DBNull.Value);
        c.Parameters.AddWithValue("$folder", (object?)rule.Folder?.ToString() ?? DBNull.Value); c.Parameters.AddWithValue("$order", rule.Order); c.Parameters.AddWithValue("$enabled", rule.Enabled);
        c.Parameters.AddWithValue("$history", rule.IncludeHistory); c.Parameters.AddWithValue("$json", JsonSerializer.Serialize(rule)); c.ExecuteNonQuery();
        tx.Commit(); return rule;
    }

    internal static ConditionTree Conditions(ReceiveRule rule)
    {
        var condition = ConditionTree.Parse(rule.Condition);
        if (string.IsNullOrWhiteSpace(rule.Exception)) return condition;
        return new(ConditionTree.CurrentVersion, new(ConditionLogic.And, null, [condition.Root, new(ConditionLogic.Not, null, [ConditionTree.Parse(rule.Exception).Root])]));
    }

    private sealed record Work(string Id, string Event, Guid Account, Guid Folder, string Item, ReceiveRule Rule);
    private Work? Claim()
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = """
            SELECT r.id,r.event,r.account,r.folder,r.item,r.definition FROM rule_run r WHERE r.state='pending'
            AND NOT EXISTS(SELECT 1 FROM rule_run p WHERE p.event=r.event AND (p.priority,p.rule)<(r.priority,r.rule) AND p.state IN ('pending','running','waiting','paused','uncertain'))
            ORDER BY r.created,r.priority,r.rule LIMIT 1
            """;
        Work? work;
        using (var r = c.ExecuteReader())
        {
            if (!r.Read()) return null;
            work = new(r.GetString(0), r.GetString(1), Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)), r.GetString(4), JsonSerializer.Deserialize<ReceiveRule>(r.GetString(5))!);
        }
        c.CommandText = "UPDATE rule_run SET state='running',detail='正在判断' WHERE id=$id"; c.Parameters.AddWithValue("$id", work.Id); c.ExecuteNonQuery(); tx.Commit(); return work;
    }

    private void Finish(Work work, string state, string detail, bool stop = false)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = "UPDATE rule_run SET state=$state,detail=$detail WHERE id=$id"; c.Parameters.AddWithValue("$id", work.Id);
        c.Parameters.AddWithValue("$state", state); c.Parameters.AddWithValue("$detail", detail); c.ExecuteNonQuery();
        if (stop)
        {
            c.CommandText = "UPDATE rule_run SET state='stopped',detail='前序规则要求停止' WHERE event=$event AND state='pending'";
            c.Parameters.AddWithValue("$event", work.Event); c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public async Task<int> Process(int limit = 32, CancellationToken token = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var owner = AcquireProcessor();
        if (owner is null) return 0;
        using (var db = store.Open()) using (var c = db.CreateCommand())
        {
            c.CommandText = "UPDATE rule_run SET state='pending',detail='恢复中断的处理' WHERE state='running'"; c.ExecuteNonQuery();
        }
        int count = 0;
        while (count < limit)
        {
            token.ThrowIfCancellationRequested();
            var work = Claim(); if (work is null) break;
            try { await Execute(work, token); }
            catch (OperationCanceledException) { Finish(work, "pending", "已中断，将从动作记录恢复。"); throw; }
            catch (Exception e) { Finish(work, "failed", e.Message, true); }
            count++;
        }
        return count;
    }

    private async Task Execute(Work work, CancellationToken token)
    {
        using var db = store.Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT version FROM receive_rule WHERE id=$id AND enabled"; c.Parameters.AddWithValue("$id", work.Rule.Id.ToString());
        if (Convert.ToInt32(c.ExecuteScalar()) != work.Rule.Version) { Finish(work, "skipped", "规则已停用或改版，本次收件不采用新版本。"); return; }
        var messageId = Guid.TryParse(work.Item, out _) ? work.Item : work.Folder + "/" + work.Item;
        var tree = Conditions(work.Rule);
        if (Access(work,tree) is { } reason) { Finish(work,"paused",reason); return; }
        var journal = await Actions(work.Id,token);
        if (journal.Count == 0)
        {
            bool awaitingBody = NeedsBody(tree.Root) && MissingBody(work);
            var result = await search.Evaluate(messageId, tree, token);
            if (result == ConditionResult.Unknown && awaitingBody) { WaitForBody(work); return; }
            if (result != ConditionResult.True)
            {
                string detail=result==ConditionResult.Unknown ? "条件所需字段缺失，未执行动作。" : "邮件不满足匹配条件。";
                if (result==ConditionResult.False && !string.IsNullOrWhiteSpace(work.Rule.Exception) &&
                    await search.Evaluate(messageId,ConditionTree.Parse(work.Rule.Exception),token)==ConditionResult.True) detail="被例外条件排除：" + work.Rule.Exception;
                Finish(work, result == ConditionResult.False ? "no-match" : "unknown",detail, result == ConditionResult.Unknown && work.Rule.Stop);
                return;
            }
        }
        await ApplyActions(work,journal,token);
    }
}

public sealed class ReceiveRuleRunner : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public ReceiveRuleRunner(IReceiveRules service, Action<string>? notice)
    {
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    try { await service.Process(32, _stop.Token); }
                    catch (Exception e) when (e is not OperationCanceledException) { notice?.Invoke("收件规则处理失败，请查看规则记录。"); }
                } while (await timer.WaitForNextTickAsync(_stop.Token));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }
    public void Dispose() { _stop.Cancel(); _loop.GetAwaiter().GetResult(); _stop.Dispose(); }
}
