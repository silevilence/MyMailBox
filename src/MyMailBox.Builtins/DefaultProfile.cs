using CordiSharp;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public static class DefaultProfile
{
    public static PluginConfiguration Core => new(1, [new("platform", "core.platform", "1.0.0")]);
    public static PluginConfiguration Ordinary => new(1, [new("welcome", "welcome", "1.0.0"), new("prototype", "prototype", "1.0.0"), new("selector", "selector", "1.0.0"), new("local-mail", "local-mail", "1.0.0"), new("accounts", "accounts", "1.0.0"), new("mail-selectors", "mail-selectors", "1.0.0"), new("reminders", "reminders", "1.0.0"), new("updates", "updates", "1.0.0")]);

    public static PluginCatalog Catalog(string? workspace = null, IMailStore? mailStore = null, IMimeReader? mimeReader = null, Action<string>? openWorkspace = null, IMailPdfPrinter? printer = null, Action<string>? notification = null, Action<Updates.PreparedUpdate>? installUpdate = null)
    {
        var catalog = new PluginCatalog();
        var location = workspace ?? Environment.GetEnvironmentVariable("MYMAILBOX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMailBox");
        catalog.Add(new(new("updates", "1.0.0", 1, 1, PluginKind.Ordinary, "updates"), PluginOrigin.Builtin, "", (ctx, _) => UpdatePlugin.Apply(ctx, location, notification, install: installUpdate)));
        catalog.Add(new(new("reminders", "1.0.0", 1, 1, PluginKind.Ordinary, "reminders"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var host = ctx.Get<IPluginHost>("mailbox.host")!; var store = new Mail.SqliteMailStore(Path.Combine(location, "mail"));
            var sink = new Mail.SqliteNotificationSink(store, notice => notification?.Invoke("提醒：" + notice.Title + "。可到“提醒与计划”延后或关闭。"));
            var scheduler = new Mail.ReminderService(store, sink);
            var recurring = new Mail.ScheduleService(store, new Mail.RecurrencePlanner(), sink);
            ctx.Effect(() => host.Add(new Page("list", "提醒与计划", "", [], [], (_, _) => Task.FromResult("")) { Reminders = new(scheduler, new Mail.AvailableMailStore(store)) }));
            ctx.Effect(() => host.Add(new Page("recurring", "循环计划", "", [], [], (_, _) => Task.FromResult("")) { Schedules = recurring }));
            ctx.Effect(() => new Mail.ReminderRunner(scheduler, notification));
            ctx.Effect(() => new Mail.ScheduleRunner(recurring, notification));
        }));
        catalog.Add(new(new("mail-selectors", "1.0.0", 1, 1, PluginKind.Ordinary, "mail-selectors"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var host = ctx.Get<IPluginHost>("mailbox.host")!;
            var store = new Mail.SqliteMailStore(Path.Combine(location, "mail"));
            var service = new Mail.SelectionService(store, new Mail.AccountService(store));
            ctx.Effect(() => host.Add(new Page("choose", "写信与整理", "", [], [], (_, _) => Task.FromResult(""), Selections: service)));
        }));
        catalog.Add(new(new("accounts", "1.0.0", 1, 1, PluginKind.Ordinary, "accounts"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var host = ctx.Get<IPluginHost>("mailbox.host")!;
            var store = new Mail.SqliteMailStore(Path.Combine(location, "mail"));
            var accounts = new Mail.AccountService(store);
            var protocols = new Mail.ProtocolMailService(store);
            var inbox = new Mail.UnifiedInboxService(store, accounts, new Mail.ProtocolReceiveProvider(store, protocols));
            ctx.Effect(() => host.Add(new Page("inbox", "统一收件箱", "", [], [], (_, _) => Task.FromResult("")) { UnifiedInbox = inbox }));
            ctx.Effect(() => host.Add(new Page("organize", "目录与标记", "", [], [], (_, _) => Task.FromResult("")) { Organization = new(accounts, protocols) { Actions = protocols, Transfers = protocols } }));
            ctx.Effect(() => new Mail.ReceiveRunner(inbox, notification));
            ctx.Effect(() => new Mail.OutboxRunner(protocols, notification));
            var graph = new Mail.GraphMailService(new Mail.SqliteMailStore(Path.Combine(location, "mail")));
            ctx.Effect(() => graph);
            ctx.Effect(() => host.Add(new Page("graph", "Outlook.com 邮件", "", [], [], (_, _) => Task.FromResult("")) { Graph = new(accounts, graph) }));
            ctx.Effect(() => host.Add(new Page("manage", "账户管理", "", [], [], (_, _) => Task.FromResult(""), Accounts: accounts)));
            ctx.Effect(() => host.Add(new Page("protocols", "邮箱连接与收发", "", [], [], (_, _) => Task.FromResult(""))
                { Protocols = new(accounts, protocols) { ImapSync = protocols, Outbox = protocols } }));
        }));
        catalog.Add(new(new("local-mail", "1.0.0", 1, 1, PluginKind.Ordinary, "local-mail"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var localStore = new Mail.SqliteMailStore(Path.Combine(location, "mail"));
            var remote = new Mail.RemoteContentService(localStore); ctx.Effect(() => remote);
            var available = mailStore ?? new Mail.AvailableMailStore(localStore);
            LocalMailPlugin.Apply(ctx, available, mimeReader ?? new Mail.MimeReader(), remote, printer);
            var exportHost = ctx.Get<IPluginHost>("mailbox.host")!;
            ctx.Effect(() => exportHost.Add(new Page("export", "批量导出", "", [], [], (_, _) => Task.FromResult(""))
                { Export = new(available, new Mail.SelectionService(localStore, new Mail.AccountService(localStore))) }));
            ctx.Effect(() => exportHost.Add(new Page("local-mailboxes", "本地邮箱管理", "", [], [], (_, _) => Task.FromResult(""))
                { LocalMailboxes = new Mail.LocalMailboxService(localStore, new Mail.AccountService(localStore), mimeReader ?? new Mail.MimeReader()) }));
            ArchivePage.Apply(ctx, new Mail.SqliteMailStore(Path.Combine(location, "mail")));
            var maintenance = new Mail.StorageMaintenance(new Mail.SqliteMailStore(Path.Combine(location, "mail")), mimeReader ?? new Mail.MimeReader());
            var host = ctx.Get<IPluginHost>("mailbox.host")!;
            var backup = new Mail.WorkspaceBackup(location, new Mail.SqliteMailStore(Path.Combine(location, "mail")));
            string? restoredWorkspace = null;
            ctx.Effect(() => host.Add(new Page("backup", "工作空间备份", "备份包含账户、原文、目录、选择与插件配置；不包含账户凭据和可丢弃缓存。恢复到新的空位置，保留稳定引用，在线账户需重新授权。",
                [new("backup-file", "备份文件（新建时填新文件名；恢复时选择已有文件）", FilePicker: true), new("restore-directory", "恢复到尚不存在的新目录")],
                [new("backup-create", "创建一致性备份"), new("backup-restore", "校验并恢复到新目录"), new("backup-open", "在新窗口打开恢复工作空间")], async (values, action) =>
                {
                    if (action == "backup-create") return "备份已完成：" + await backup.Create(values["backup-file"]);
                    if (action == "backup-restore")
                    {
                        restoredWorkspace = await backup.Restore(values["backup-file"], values["restore-directory"]);
                        return "已恢复到：" + restoredWorkspace + "。可在新窗口打开并检查恢复结果；当前工作空间保持不变。";
                    }
                    if (restoredWorkspace is null || openWorkspace is null) return "请先成功恢复一个工作空间，再打开它。";
                    openWorkspace(restoredWorkspace); return "恢复工作空间已在新窗口打开。";
                })));
            ctx.Effect(() => host.Add(new Page("storage", "存储与恢复", "检查原文与索引。清理仅删除可重新生成的缓存，所有本地原文和独立归档均保留。", [],
                [new("storage-check", "检查完整性"), new("storage-rebuild", "从原文重建摘要与索引"), new("storage-clear", "清理可丢弃缓存")], async (_, action) => action switch
                {
                    "storage-check" => (await maintenance.Inspect()).ToString(),
                    "storage-rebuild" => $"已重建 {await maintenance.Rebuild()} 封邮件的摘要与索引。原文保持不变。",
                    _ => $"已释放可丢弃缓存 {await maintenance.ClearCache():N0} 字节；所有原文保留。"
                })));
        }));
        catalog.Add(new(new("selector", "1.0.0", 1, 1, PluginKind.Ordinary, "selector"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var host = ctx.Get<IPluginHost>("mailbox.host")!;
            ctx.Effect(() => host.AddSelector(new("accounts", "默认账户选择器", () => PrototypePlugin.Accounts)));
            ctx.Effect(() => host.Add(new Page("selector", "选择器示例", "账户选项由可替换的普通插件提供。", [new("selected-account", "账户", "work", Selector: "accounts")],
                [new("confirm-selection", "确认选择")], (values, _) => Task.FromResult("稳定账户 ID：" + values["selected-account"]))));
        }));
        catalog.Add(new(new("prototype", "1.0.0", 1, 1, PluginKind.Ordinary, "prototype"), PluginOrigin.Builtin, "", PrototypePlugin.Apply));
        catalog.Add(new(new("core.platform", "1.0.0", 1, 1, PluginKind.Core, "platform"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var host = ctx.Get<IPluginHost>("mailbox.host")!;
            ctx.Effect(() => host.Command("environment", _ => Task.FromResult("Windows x64 / .NET " + Environment.Version)));
        }));
        catalog.Add(new(new("welcome", "1.0.0", 1, 1, PluginKind.Ordinary, "welcome"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var host = ctx.Get<IPluginHost>("mailbox.host")!;
            ctx.Effect(() => host.Add(new Page("welcome", "欢迎", "MyMailBox 0.1.0\n你的邮件工作空间，从这里开始。", [], [], (_, _) => Task.FromResult(""))));
        }));
        return catalog;
    }

    public static void Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!File.Exists(Path.Combine(directory, "core.json"))) ConfigurationFiles.Save(Path.Combine(directory, "core.json"), Core);
        if (!File.Exists(Path.Combine(directory, "plugins.json"))) ConfigurationFiles.Save(Path.Combine(directory, "plugins.json"), Ordinary);
    }

    public static IDisposable Recovery(Contributions pages, string error, string directory) => pages.Add(new Page("recovery", "启动修复", error,
        [], [new("open-profile", "打开配置目录"), new("safe-default", "备份并恢复默认核心配置")], (_, action) =>
        {
            if (action == "open-profile") System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            else
            {
                var path = Path.Combine(directory, "core.json");
                if (File.Exists(path)) File.Copy(path, path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                ConfigurationFiles.Save(path, Core);
            }
            return Task.FromResult("修复后请关闭并重新启动应用。当前安全模式保留。 ");
        }));
}
