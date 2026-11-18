using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class SearchChecks
{
    public static async Task Builder()
    {
        var f = await Fixture();
        var tree = ConditionTree.Parse("""(subject:"采购" OR body:"甲\"乙") AND NOT star=false""");
        var formatted = ConditionEditor.Expression(tree.Root);
        var reconstructed = ConditionTree.Parse(formatted);
        MailChecks.Check(await f.Search.Evaluate(f.Hit.ToString(), tree) == await f.Search.Evaluate(f.Hit.ToString(), reconstructed) &&
            ConditionEditor.Describe(tree.Root, f.Search.Fields).Contains("并且"), "Visual/advanced roundtrip diverged");
        var reduced = ConditionEditor.Replace(tree.Root, tree.Root.Children[1], null)!;
        MailChecks.Check(reduced.Logic == ConditionLogic.Or, "Group removal failed");
        var scope = (await f.Search.Library()).Scopes.Single(s => s.Folder == SqliteMailStore.Inbox);
        var saved = new SavedSearch(Guid.NewGuid(), "采购范围", formatted, SearchOrder.Largest, scope.Account, scope.Folder);
        await f.Search.Save(saved);
        var reopened = new MailSearchService(f.Store,f.Protocols);
        using var db = f.Store.Open(); using var c = db.CreateCommand();
        c.CommandText = "UPDATE folders SET path='已重命名' WHERE id=$id"; c.Parameters.AddWithValue("$id", scope.Folder!.Value.ToString()); c.ExecuteNonQuery();
        var library = await reopened.Library(); MailChecks.Check(library.Saved.Single() == saved && saved.HasScope(library) && library.Scopes.Any(s=>s.Name.Contains("已重命名")), "Saved reference broke on rename");
        MailChecks.Check(await reopened.EvaluateSaved(f.Hit.ToString(), saved.Id) == ConditionResult.True, "Automation lost saved reference");
        c.CommandText = "UPDATE accounts SET enabled=0 WHERE id=$account"; c.Parameters.AddWithValue("$account", scope.Account!.Value.ToString()); c.ExecuteNonQuery();
        MailChecks.Check(!saved.HasScope(await reopened.Library()) && await reopened.EvaluateSaved(f.Hit.ToString(),saved.Id) == ConditionResult.Unknown, "Missing account broadened search");
        var repair = saved with { Folder=null, Account=null, Expression="""NOT plugin.example:"a \"b" """ };
        await reopened.Save(repair);
        library = await reopened.Library();
        MailChecks.Check(library.Saved.Single() == repair && repair.HasScope(library), "Repair discarded extension expression or id");
        Console.WriteLine("PASS visual condition roundtrip, nested edit, readable summary, saved scope rename/repair, retained extension and automation reference");
    }
}
