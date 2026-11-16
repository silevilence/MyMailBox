using MyMailBox.Builtins;

internal static class PrototypeChecks
{
    public static async Task Run()
    {
        var pages = PrototypePlugin.CreatePages();
        var read = pages.Single(p => p.Id == "read");
        foreach (var account in PrototypePlugin.Accounts)
        {
            var values = new Dictionary<string, string> { ["read-account"] = account.Id };
            PluginChecks.Check((await read.Execute(values, "read-message")).Contains(account.Label), "Reading lost account identity");
            PluginChecks.Check((await read.Execute(values, "read-error")).Contains("保留当前选择"), "Error recovery missing");
        }
        var compose = pages.Single(p => p.Id == "compose");
        var draft = compose.Fields.ToDictionary(f => f.Id, f => f.Value); draft["send-account"] = "personal";
        await compose.Execute(draft, "save-draft");
        PluginChecks.Check((await compose.Execute(draft, "restore-draft")).Contains("alex@personal.example"), "Draft identity lost");
        var automate = pages.Single(p => p.Id == "automation"); var plan = automate.Fields.ToDictionary(f => f.Id, f => f.Value);
        PluginChecks.Check((await automate.Execute(plan, "run-plan")).Contains("先预览"), "Skipped scope preview");
        var preview = await automate.Execute(plan, "preview-plan");
        PluginChecks.Check(preview.Contains("工作") && preview.Contains("处理来源：个人") && preview.Contains("本地归档"), "Trigger and target confused");
        await automate.Execute(plan, "fail-plan");
        var recovery = pages.Single(p => p.Id == "recovery");
        var result = await recovery.Execute(plan, "resume-plan");
        PluginChecks.Check(result.Contains("仅继续第 3 封") && result == await recovery.Execute(plan, "resume-plan"), "Recovery duplicates work");
        plan["age-days"] = "100";
        PluginChecks.Check((await automate.Execute(plan, "run-plan")).Contains("选择已变化"), "Stale preview accepted");
        Console.WriteLine("PASS prototype: reading/accounts, draft identity, cross-account scope, interrupted recovery, stale preview guard");
    }
}
