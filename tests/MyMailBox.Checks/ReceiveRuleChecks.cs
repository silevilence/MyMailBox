using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Text;

internal static partial class ReceiveRuleChecks
{
    private static async Task<Guid> Import(SqliteMailStore store, string subject)
    {
        var raw = Encoding.UTF8.GetBytes("From: buyer@example.test\r\nTo: office@example.test\r\nSubject: " + subject + "\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nLocal fixture body.");
        return await store.Import(SqliteMailStore.Inbox, raw, await new MyMailBox.Mail.MimeReader().Parse(raw));
    }
    public static async Task MainRules()
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/rules-" + Guid.NewGuid().ToString("N")));
        var protocols = new ProtocolMailService(store); var search = new MailSearchService(store, protocols); var service = new ReceiveRuleService(store, search, protocols);
        var target = await protocols.Create(SqliteMailStore.LocalAccount, null, "采购", default);
        var rule = new ReceiveRule(Guid.NewGuid(), 0, "采购收件", SqliteMailStore.LocalAccount, SqliteMailStore.Inbox, "subject:Order AND from:buyer", "subject:private",
            [new(RuleActionKind.Star), new(RuleActionKind.Label, "采购"), new(RuleActionKind.Move, Destination: target)], 5, true, true, false);
        rule = await service.Save(rule);
        await Import(store, "Order-old"); MailChecks.Check(await service.Process() == 0, "Historical import executed by default");
        rule = await service.Save(rule with { IncludeHistory = true });
        await service.Save(rule with { Id = Guid.NewGuid(), Version = 0, Name = "随后", Order = 10, Actions = [new(RuleActionKind.Read)] });
        var hit = await Import(store, "Order 01"); await Import(store, "Order private");
        await service.Process();
        var dashboard = await service.Dashboard();
        MailChecks.Check(dashboard.Runs.Count(r => r.State == "applied") == 1 && dashboard.Runs.Count(r => r.State == "stopped") == 1 &&
            dashboard.Runs.Count(r => r.State == "no-match") == 2, "Ordering, exception or stop failed: " + string.Join("/", dashboard.Runs.Select(r => r.Detail)));
        MailChecks.Check((await protocols.Messages(SqliteMailStore.LocalAccount, target, 0, 10)).Items.Single().Key == hit.ToString(), "Matched mail did not move");
        await service.Save(rule with { Enabled = false });
        MailChecks.Check(!(await service.Dashboard()).Rules.Single(r => r.Id == rule.Id).Enabled, "Rule could not be disabled");
        Console.WriteLine("PASS receive rule save/revise/disable, scope, opt-in history, exception, ordered actions, move and stop with durable outcomes");
    }
}
