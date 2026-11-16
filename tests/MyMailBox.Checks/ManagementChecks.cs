using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

internal static class ManagementChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(PluginChecks.Repository, "artifacts/checks/management-" + Guid.NewGuid().ToString("N"));
        DefaultProfile.Ensure(directory);
        var catalog = DefaultProfile.Catalog();
        var dependent = PluginChecks.Example() with { Manifest = PluginChecks.Example().Manifest with { Id = "dependent", Dependencies = ["missing.service"] } };
        catalog.Add(dependent);
        var pages = new Contributions(); await using var runtime = new PluginRuntime(pages);
        await using var config = new HotConfiguration(runtime, catalog, new HashSet<string>(), Path.Combine(directory, "plugins.json"));
        await config.Save(new(1, [new("welcome", "welcome", "1.0.0")]));
        using var manager = PluginManager.Register(pages, new(Path.Combine(directory, "packages"), catalog), catalog, config, Path.Combine(directory, "core.json"), runtime);
        var page = pages.Pages.Single(p => p.Id == "plugin-manager");
        var values = page.Fields.ToDictionary(p => p.Id, p => p.Value);
        values["plugin-id"] = "dependent"; values["managed-instance"] = "dependent";
        await PluginChecks.Reject(() => page.Execute(values, "enable-plugin"), "missing.service");
        PluginChecks.Check(config.Current.Instances.Single(p => p.Id == "dependent").Enabled == false && runtime.Running.Contains("welcome"), "Missing dependency discarded config or affected app");
        var platform = catalog.Find(DefaultProfile.Core.Instances[0]);
        catalog.Add(platform with { Manifest = platform.Manifest with { Version = "2.0.0" } });
        var core = new CoreProfile(runtime, catalog, Path.Combine(directory, "core.json")); await core.Start();
        values["plugin-id"] = "core.platform"; values["managed-instance"] = "platform"; values["plugin-version"] = "2.0.0";
        PluginChecks.Check((await page.Execute(values, "upgrade-plugin")).Contains("下次启动"), "Core update not deferred");
        PluginChecks.Check(ConfigurationFiles.Read(Path.Combine(directory, "core.json")).Instances.Single().Version == "2.0.0" && runtime.States["platform"].Contains("1.0.0"), "Core replaced live");
        Console.WriteLine("PASS management: missing dependency retains disabled configuration, existing app preserved, core upgrade saved for restart");
    }
}
