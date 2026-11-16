using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

internal static class UpgradeChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(PluginChecks.Repository, "artifacts/checks/upgrade-" + Guid.NewGuid().ToString("N"));
        DefaultProfile.Ensure(folder);
        var catalog = DefaultProfile.Catalog(); catalog.Add(PluginChecks.Example());
        var v2 = PluginChecks.Example() with { Directory = Path.Combine(PluginChecks.Repository, "samples/ExamplePluginV2/bin/Debug/net10.0"), Manifest = PluginChecks.Example().Manifest with { Version = "2.0.0" } };
        catalog.Add(v2);
        await using var runtime = new PluginRuntime(new Contributions());
        await using var hot = new HotConfiguration(runtime, catalog, new HashSet<string>(), Path.Combine(folder, "plugins.json"));
        var initial = new PluginConfiguration(1, [new("example", "example", "1.0.0")]); await hot.Save(initial);
        var finished = 0; using var subscription = runtime.Events.Subscribe(value => { if (value == "work-complete") finished++; });
        var scope = runtime.Scope("example"); var work = scope.Execute("work");
        var upgrading = hot.Save(new(1, [new("example", "example", "2.0.0")]));
        await PluginChecks.Reject(() => scope.Execute("ping"), "停止接单");
        await work; await upgrading;
        PluginChecks.Check(finished == 1 && (await runtime.Scope("example").Execute("ping")).Contains("版本2"), "Drain/real version change failed");
        await PluginChecks.Reject(() => hot.Save(new(1, [new("example", "example", "1.0.0", Options: new() { FailInitialization = true })])), "上次有效");
        PluginChecks.Check((await runtime.Scope("example").Execute("ping")).Contains("版本2"), "Old version not restored");
        var cancellation = runtime.Scope("example").Execute("work");
        await runtime.Stop("example", cancel: true);
        await PluginChecks.Reject(() => cancellation, "canceled");
        await runtime.Start(v2, new() { InstanceId = "held" });
        var held = runtime.Scope("held").Execute("work");
        await PluginChecks.Reject(() => runtime.Stop("held", timeout: TimeSpan.FromMilliseconds(10)), "停止操作失败");
        PluginChecks.Check(runtime.States["held"].Contains("尚未释放"), "Stop failure claimed unload");
        await held; await runtime.Stop("held");
        Console.WriteLine("PASS upgrade: real v1/v2, drain/reject new work, single completion, initialization rollback, cancellation, stop timeout diagnosis");
    }
}
