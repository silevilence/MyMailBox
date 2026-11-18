using MyMailBox.Contracts;
using System.Text.Json;

internal static partial class SearchChecks
{
    public static async Task Conditions()
    {
        var f = await Fixture();
        Task<SearchPage> Find(string expr) => f.Search.Search(new([]) { Condition = ConditionTree.Parse(expr) });
        foreach (var expr in new[] { "subject^=采购 AND subject$=合同", "(subject=旧邮件 OR body~采[购买]) AND star=true",
            "sent>=2025-04-02", "bcc?", "NOT (subject=旧邮件 OR NOT body:报价)", "subject=采购合同 OR subject=旧邮件 AND NOT read=false" })
            MailChecks.Check((await Find(expr)).Items.Single().LocalId == f.Hit, expr);
        var absent = await Find("NOT bcc:hidden"); MailChecks.Check(absent.Unknown == 1 && absent.Items.Count == 1, "Negated missing Bcc matched");
        var inactive = await Find("subject:采购 OR NOT plugin.demo:yes");
        MailChecks.Check(inactive.Items.Count == 0 && inactive.Unknown == 2 && inactive.UnavailableFields.Single() == "plugin.demo", "Inactive predicate changed targets");
        var tree = JsonSerializer.Deserialize<ConditionTree>(JsonSerializer.Serialize(ConditionTree.Parse("body:合同 AND star=true")))!;
        MailChecks.Check(await f.Search.Evaluate(f.Hit.ToString(), tree) == ConditionResult.True &&
            await f.Search.Evaluate("absent", tree) == ConditionResult.Unknown, "Serialized automation condition diverged");
        MailChecks.Check(ConditionTree.Parse("""subject="a \"b\\c" """).Root.Criterion!.Value == "a \"b\\c", "Escaping changed");
        foreach (var (expr, pos) in new[] { ("(subject:1",10), ("subject:1 AND",13), ("subject??",8), ("typo:a",0), ("subject~[",0) })
        {
            try { await Find(expr); throw new Exception("Invalid expression accepted: " + expr); }
            catch (ConditionException e) { MailChecks.Check(e.Position == pos, "Wrong error position for " + expr); }
        }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { await f.Search.Evaluate(f.Hit.ToString(), ConditionTree.Parse("subject~.*"), stop.Token); throw new Exception("Cancelled regex ran"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS query grammar, escaped literals, precedence, 3-valued logic, bounded regex, versioned search/automation semantics");
    }
}
