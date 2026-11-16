using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

internal static class ConfigurationChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(PluginChecks.Repository, "artifacts/checks/config-" + Guid.NewGuid().ToString("N"));
        DefaultProfile.Ensure(folder);
        var path = Path.Combine(folder, "plugins.json");
        var catalog = DefaultProfile.Catalog(); catalog.Add(PluginChecks.Example());
        await using var runtime = new PluginRuntime(new Contributions());
        await using var hot = new HotConfiguration(runtime, catalog, new HashSet<string>(), path);
        PluginChecks.Check(await hot.Reload(), "Initial config failed");
        var config = new PluginConfiguration(1, [new("one", "example", "1.0.0"), new("two", "example", "1.0.0")]);
        await hot.Save(config);
        await hot.Reload(); await hot.Reload();
        PluginChecks.Check(hot.AppliedCount == 2 && runtime.Running.Count == 2, "Duplicate event changed runtime");
        File.WriteAllText(path, "{\"schemaVersion\":");
        PluginChecks.Check(!await hot.Reload() && runtime.Running.Count == 2, "Partial write removed valid state");
        File.WriteAllText(path, "{\"schemaVersion\":99,\"instances\":[]}");
        PluginChecks.Check(!await hot.Reload() && hot.Error!.Contains("schemaVersion"), "Schema error not located");
        var failed = config with { Instances = [config.Instances[0] with { Options = new() { FailInitialization = true } }] };
        await PluginChecks.Reject(() => hot.Save(failed), "上次有效");
        PluginChecks.Check(runtime.Running.Count == 2, "Failed activation did not restore old instances");
        ConfigurationFiles.Save(path, config with { SchemaVersion = 0, Instances = [config.Instances[0] with { Options = new() { Greeting = "更新" } }] });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((hot.Current.Instances.Length != 1 || hot.Error is not null) && DateTime.UtcNow < deadline) await Task.Delay(50);
        PluginChecks.Check(hot.Current.SchemaVersion == 1 && runtime.Running.Count == 1 && (await runtime.Scope("one").Execute("ping")).Contains("更新"), "Watcher/migration failed");
        Console.WriteLine("PASS configuration: atomic GUI/file changes, watcher, duplicate events, partial/schema errors, two instances, activation rollback, v0 migration");
    }
}
