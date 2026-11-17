using MyMailBox.Contracts;
using MyMailBox.Runtime;

internal static class PluginChecks
{
    public static string Repository => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    public static PluginDefinition Example(PluginKind kind = PluginKind.Ordinary) => new(
        new("example", "1.0.0", 1, 1, kind, "example", "ExamplePlugin.dll"), PluginOrigin.External,
        Path.Combine(Repository, "samples/ExamplePlugin/bin/Debug/net10.0"));

    public static async Task Run()
    {
        var pages = new Contributions();
        await using var runtime = new PluginRuntime(pages);
        var definition = Example();
        for (var i = 0; i < 30; i++)
        {
            await runtime.Start(definition, new() { InstanceId = "example" });
            runtime.Events.Emit("check");
            var value = await runtime.Scope("example").Execute("ping");
            Check(value.Contains("私有依赖") && value.Contains("events=1;"), "Shared contracts, private dependency or events failed");
            var retainedPage = pages.Pages.Single();
            await runtime.Stop("example");
            await Reject(() => retainedPage.Execute(new Dictionary<string, string>(), "ping"), "停止接单");
            GC.KeepAlive(retainedPage);
            Check(runtime.Events.SubscriptionCount == 0 && pages.Pages.Count == 0, "Leaked subscriptions/pages");
            using var file = File.Open(Path.Combine(definition.Directory, "ExamplePlugin.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        await runtime.Start(definition with { Manifest = definition.Manifest with { Entry = "typed" } }, new() { InstanceId = "typed" });
        var retainedService = pages.Pages.Single().Updates!;
        Check((await retainedService.Snapshot()).CurrentVersion == "1.0.0", "Typed service failed before unload");
        await runtime.Stop("typed");
        await Reject(() => retainedService.Snapshot(), "停止接单");
        GC.KeepAlive(retainedService);
        await Reject(() => runtime.Start(definition with { Manifest = definition.Manifest with { SdkMin = 2 } }, new() { InstanceId = "bad" }), "SDK");
        Check(runtime.Running.Count == 0, "Incompatible plugin affected host");
        Console.WriteLine("PASS plugins: 30 load/call/unload cycles, retained page and typed service release, GC verification, file locks, private dependency, incompatible SDK");
    }
    public static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Reject(Func<Task> action, string fragment)
    {
        try { await action(); }
        catch (Exception e) when (e.ToString().Contains(fragment)) { return; }
        throw new Exception("Expected rejection: " + fragment);
    }
}
