using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MyMailBox.Builtins;
using MyMailBox.Runtime;

namespace MyMailBox.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var pages = new Contributions(action =>
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
                else Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
            });
            var workspace = Environment.GetEnvironmentVariable("MYMAILBOX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMailBox");
            var diagnostics = new Diagnostics(Path.Combine(workspace, "logs"));
            var applicationVersion = System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
            var runtime = new PluginRuntime(pages, diagnostics: diagnostics);
            DesktopLifetime? lifetime=null;
            HotConfiguration? hotConfiguration = null;
            IDisposable? settings = null;
            IDisposable? manager = null;
            IDisposable? diagnosticsPage = null;
            MyMailBox.Updates.PreparedUpdate? pendingUpdate = null;
            PluginCatalog? activeCatalog = null;
            PluginConfiguration? activeCore = null;
            desktop.MainWindow = new ShellWindow(pages);
            if(desktop.Args?.Contains("--measure-frames")==true)FrameMeasurements.Attach(desktop.MainWindow,workspace);
            var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                var directory = Path.Combine(workspace, "profiles", "default");
                try
                {
                    DefaultProfile.Ensure(directory);
                    var catalog = DefaultProfile.Catalog(workspace, openWorkspace: restored =>
                    {
                        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                        start.Environment["MYMAILBOX_HOME"] = restored;
                        System.Diagnostics.Process.Start(start);
                    }, presenters:()=>new[]{new MyMailBox.Contracts.NotificationPresenter("builtin","应用内通知",(batch,_)=>{Avalonia.Threading.Dispatcher.UIThread.Post(()=>{pages.ShowActivity(batch);});return Task.CompletedTask;})}.Concat(pages.Presenters).ToArray(),
                        templateConfiguration:()=>hotConfiguration?.Current ?? new(1,[]),
                        saveTemplateConfiguration: config=>(hotConfiguration ?? throw new InvalidOperationException("插件配置尚未就绪。")).Save(config),
                        mailboxProviders:()=>pages.MailboxProviders,automationExtensions:()=>pages.AutomationExtensions,locked:()=>AutomationSystemEvents.IsLocked,searchExtensions: () => pages.SearchExtensions, printer: new WebViewPdfPrinter(), notification: message => Avalonia.Threading.Dispatcher.UIThread.Post(() => pages.Notice = message), installUpdate: package =>
                    {
                        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "update-host", "MyMailBox.UpdateHost.exe"))) throw new InvalidOperationException("当前运行目录缺少更新助手，请从完整安装包运行应用。");
                        pendingUpdate = package;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.MainWindow.Close());
                    });
                    activeCatalog = catalog;
                    var pluginDirectory = Path.Combine(workspace, "plugins");
                    var packages = new PluginPackages(pluginDirectory, catalog);
                    var packageErrors = packages.Discover();
                    var core = new CoreProfile(runtime, catalog, Path.Combine(directory, "core.json"));
                    await core.Start();
                    if (core.Error is null) activeCore = ConfigurationFiles.Read(Path.Combine(directory, "core.json"));
                    if (core.Error is not null) DefaultProfile.Recovery(pages, core.Error, directory);
                    else
                    {
                        hotConfiguration = new HotConfiguration(runtime, catalog, core.ActivePlugins, Path.Combine(directory, "plugins.json"));
                        await hotConfiguration.Reload();
                        settings = PluginSettings.Register(pages, hotConfiguration);
                        if (hotConfiguration.Error is not null) pages.Notice = hotConfiguration.Error;
                        manager = PluginManager.Register(pages, packages, catalog, hotConfiguration, Path.Combine(directory, "core.json"), runtime);
                        diagnosticsPage = DiagnosticsPage.Register(pages, runtime, hotConfiguration, catalog, diagnostics, workspace);
                        if (packageErrors.Count > 0) pages.Notice = string.Join("\n", packageErrors);
                    }
                    if (MyMailBox.Updates.UpdateHandoff.ReadState(workspace)?.State == "awaiting-startup") _ = new MyMailBox.Mail.SqliteMailStore(Path.Combine(workspace, "mail"));
                    MyMailBox.Updates.UpdateHandoff.MarkStartup(workspace, applicationVersion, core.Error is null && hotConfiguration?.Error is null, core.Error ?? hotConfiguration?.Error);
                    var upgrade = MyMailBox.Updates.UpdateHandoff.ReadState(workspace);
                    if (upgrade?.State == "failed") pages.Notice = upgrade.Message + "\n恢复备份：" + upgrade.Backup;
                }
                catch (Exception error)
                {
                    MyMailBox.Updates.UpdateHandoff.MarkStartup(workspace, applicationVersion, false, error.Message);
                    var upgrade = MyMailBox.Updates.UpdateHandoff.ReadState(workspace);
                    DefaultProfile.Recovery(pages, error.Message + (upgrade is null ? "" : "\n更新恢复备份：" + upgrade.Backup), directory);
                }
                finally
                {
                    try{lifetime=new DesktopLifetime(desktop.MainWindow,pages,workspace);if(desktop.Args?.Contains("--tray")==true)desktop.MainWindow.Hide();}
                    catch(Exception e){pages.Notice="托盘未能初始化："+e.Message;}
                    initialized.TrySetResult();
                }
            });
            var closing = false;
            var readyToClose = false;
            desktop.MainWindow.Closing += async (_, args) =>
            {
                if (readyToClose) return;
                args.Cancel = true;
                if (closing) return;
                if(pendingUpdate is null && lifetime?.ShouldHide(args)==true){desktop.MainWindow.Hide();return;}
                closing = true;
                try
                {
                    await initialized.Task;
                    if (pendingUpdate is { } update)
                    {
                        var enabled = (activeCore?.Instances ?? []).Concat(hotConfiguration?.Current.Instances ?? []).Where(p => p.Enabled);
                        foreach (var item in enabled)
                        {
                            var plugin = activeCatalog!.Find(item);
                            if (plugin.Origin == MyMailBox.Contracts.PluginOrigin.External && (plugin.Manifest.SdkMin > update.PluginSdk || plugin.Manifest.SdkMax < update.PluginSdk))
                                throw new InvalidOperationException($"插件 {item.Id} 不兼容新版本 SDK {update.PluginSdk}，请先更新或停用该插件。");
                        }
                    }
                    using var saving = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    foreach (var participant in pages.UpdatePreparations) await participant.Save(saving.Token);
                    if (hotConfiguration is not null) await hotConfiguration.DisposeAsync();
                    settings?.Dispose(); manager?.Dispose(); diagnosticsPage?.Dispose();
                    await runtime.DisposeAsync();
                    if (pendingUpdate is { } ready)
                    {
                        var backup = Path.Combine(workspace, "updates", "backups", "before-" + ready.Version + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".zip");
                        var store = new MyMailBox.Mail.SqliteMailStore(Path.Combine(workspace, "mail"));
                        await new MyMailBox.Mail.WorkspaceBackup(workspace, store).Create(backup);
                        MyMailBox.Updates.UpdateHandoff.Launch(ready, workspace, backup, AppContext.BaseDirectory);
                    }
                    lifetime?.Dispose();readyToClose = true; desktop.MainWindow.Close();
                }
                catch (Exception error)
                {
                    pages.Notice = (pendingUpdate is null ? "关闭未完成：" : "更新准备失败，未执行安装；部分插件可能已停止，请关闭后重启。") + error.Message;
                    pendingUpdate = null; closing = false;
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
