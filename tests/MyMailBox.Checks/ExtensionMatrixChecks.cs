using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static class ExtensionMatrixChecks
{
    public static async Task Run()
    {
        var workspace = Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "extension-matrix");
        DefaultProfile.Ensure(workspace);
        var catalog = DefaultProfile.Catalog(workspace);
        var packages = new PluginPackages(Path.Combine(workspace, "plugins"), catalog);
        PluginDefinition Install(string id) => packages.Install(Path.Combine(PluginChecks.Repository, "artifacts/packages", id + "-1.0.0.zip"));
        var storage = Install("example-file-store");
        var feed = Install("example-feed");
        var notice = Install("example-presenter");
        var corePath = Path.Combine(workspace, "core.json");
        var profile = DefaultProfile.Core with { Instances = [.. DefaultProfile.Core.Instances, new("external-store", storage.Manifest.Id, "1.0.0", Grants: ["mail.storage"])] };
        ConfigurationFiles.Save(corePath, profile);
        var raw = MailChecks.Sample("独立存储原文");
        Guid saved = default;
        for (int boot = 0; boot < 3; boot++)
        {
            var pages = new Contributions();
            await using var runtime = new PluginRuntime(pages);
            var core = new CoreProfile(runtime, catalog, corePath);
            await core.Start();
            PluginChecks.Check(core.Error == null, core.Error ?? "");
            await runtime.Start(catalog.Find(new("local","local-mail","1.0.0")), new() { InstanceId = "local" });
            var reader = pages.Pages.Single(p => p.Id == "local/inbox").Reader!;
            var folders = await reader.Store.Folders();
            if (boot == 2)
            {
                PluginChecks.Check(folders.All(f => f.Name != "文件邮箱") && pages.MailBackend == null, "Restart did not restore the built-in storage");
                break;
            }
            var folder = folders.Single(f => f.Name == "文件邮箱");
            if (boot == 0) saved = await reader.Store.Import(folder.Id, raw, await reader.Parser.Parse(raw));
            PluginChecks.Check((await reader.Store.List(folder.Id, 0, 10)).Items.Single().Id == saved &&
                (await reader.Store.Read(saved)).SequenceEqual(raw), "External storage did not retain original MIME and folder identity");
            if (boot == 1) ConfigurationFiles.Save(corePath, DefaultProfile.Core);
            await PluginChecks.Reject(core.Start, "仅允许启动时读取");

            await runtime.Start(catalog.Find(new("updates", "updates", "1.0.0")), new() { InstanceId = "updates" });
            var updates = pages.Pages.Single(p => p.Updates != null).Updates!;
            var old = (await updates.Snapshot()).Settings;
            if (boot == 1)
                PluginChecks.Check(old.SourceId == "offline/local" && (await updates.Check(true)).State == UpdateCheckState.Unavailable,
                    "Restart lost the selected source or substituted the built-in feed");
            await runtime.Start(feed, new() { InstanceId = "offline", Grants = ["updates.check"] });
            var selection = updates.Sources.Single(s => s.Label == "离线示例更新源");
            await updates.Save(old with { SourceId = selection.Id });
            PluginChecks.Check((await updates.Check(true)).Message.Contains("离线示例已检查版本"), "Standard update page did not call the external source");
            var stale = pages.UpdateSources.Single().Source;
            await runtime.Stop("offline");
            PluginChecks.Check((await updates.Snapshot()).Last.State == UpdateCheckState.Unavailable &&
                (await updates.Check(true)).Release == null, "Removed update source returned a cached release");
            await PluginChecks.Reject(() => stale.Check("1.0.0", UpdateChannel.Stable), "停止接单");
            if (boot == 1)
            {
                await updates.Save(old with { SourceId = "github" });
                PluginChecks.Check((await updates.Snapshot()).Last.State == UpdateCheckState.NotChecked, "Switch source failed to clear cached result");
            }
            if (boot != 0) continue;
            await runtime.Start(notice, new() { InstanceId = "notice", Grants = ["notification.show"] });
            var messages = new List<string>();
            using var subscription = runtime.Events.Subscribe(messages.Add);
            var presenter = pages.Presenters.Single();
            var database = new SqliteMailStore(Path.Combine(workspace, "mail"));
            var center = new ActivityCenter(database);
            await center.Configure(new(false, true, presenter.Id));
            center.RecordError("示例事件");
            await center.Dispatch(() => true, () => pages.Presenters);
            PluginChecks.Check(messages.Any(m => m.Contains("本地摘要通知，共 1 项")), "Installed presenter was not dispatched by the activity center");
            await runtime.Stop("notice");
            await PluginChecks.Reject(() => presenter.Show(new(1,"error",1,"测试"), default), "停止接单");
        }
        Console.WriteLine("PASS extension matrix: packaged core storage + MIME reader after restart, ordinary update source selection/unload/restart, activity presenter dispatch");
    }
}
