using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

internal static class ProfileChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(PluginChecks.Repository, "artifacts/checks/profile-" + Guid.NewGuid().ToString("N"));
        DefaultProfile.Ensure(folder);
        var catalog = DefaultProfile.Catalog();
        catalog.Add(PluginChecks.Example(PluginKind.Core));
        var path = Path.Combine(folder, "core.json");
        var config = new PluginConfiguration(1, [new("external-core", "example", "1.0.0"), new("platform", "core.platform", "1.0.0")]);
        ConfigurationFiles.Save(path, config);
        await using var runtime = new PluginRuntime(new Contributions());
        var profile = new CoreProfile(runtime, catalog, path);
        await profile.Start();
        PluginChecks.Check(profile.Error is null && runtime.Running.Count == 2, "External core failed");
        File.WriteAllText(path, "{broken");
        PluginChecks.Check(runtime.Running.Count == 2, "Core changed while running");
        await PluginChecks.Reject(profile.Start, "仅允许启动");
        await runtime.Start(catalog.Find(DefaultProfile.Ordinary.Instances[0]), new() { InstanceId = "welcome" });
        await runtime.Stop("welcome");
        PluginChecks.Check(runtime.Running.Count == 2, "Builtin ordinary confused with core");
        await using var failed = new PluginRuntime(new Contributions());
        var broken = new CoreProfile(failed, catalog, path); await broken.Start();
        PluginChecks.Check(broken.Error?.Contains(path) == true && failed.Running.Count == 0, "No repair location/safe mode");
        ConfigurationFiles.Save(path, new(1, [new("missing", "absent", "1.0.0")]));
        var missing = new CoreProfile(failed, catalog, path); await missing.Start();
        PluginChecks.Check(missing.Error?.Contains("absent@1.0.0") == true, "Missing chain omitted");
        var cyclic = PluginChecks.Example(PluginKind.Core);
        catalog.Add(cyclic with { Manifest = cyclic.Manifest with { Dependencies = ["example"] } });
        ConfigurationFiles.Save(path, new(1, [new("cycle", "example", "1.0.0")]));
        var cycle = new CoreProfile(failed, catalog, path); await cycle.Start();
        PluginChecks.Check(cycle.Error?.Contains("example → example") == true, "Cycle chain omitted");
        Console.WriteLine("PASS profiles: external core, builtin ordinary, startup snapshot, safe recovery, missing version/service, cycle chain");
    }
}
