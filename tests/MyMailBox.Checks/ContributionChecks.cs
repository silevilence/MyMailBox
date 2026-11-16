using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

internal static class ContributionChecks
{
    public static async Task Run()
    {
        var pages = new Contributions();
        await using var runtime = new PluginRuntime(pages);
        var builtin = DefaultProfile.Catalog().Find(new("selector", "selector", "1.0.0"));
        var external = PluginChecks.Example() with { Manifest = PluginChecks.Example().Manifest with { Entry = "selector" } };
        for (var i = 0; i < 30; i++)
        {
            await runtime.Start(builtin, new() { InstanceId = "selector" });
            PluginChecks.Check(pages.Choices("accounts")[0].Id == "work", "Builtin provider missing");
            await runtime.Stop("selector");
            await runtime.Start(external, new() { InstanceId = "alternative" });
            PluginChecks.Check(pages.Choices("accounts")[0].Id == "local" && pages.Pages.Count == 1, "External selector failed to replace");
            await runtime.Stop("alternative");
            PluginChecks.Check(pages.Pages.Count == 0 && pages.Choices("accounts").Count == 0, "Contribution leak");
        }
        Console.WriteLine("PASS contributions: 30 builtin/external selector replacements, stable IDs, page/menu/provider cleanup");
    }
}
