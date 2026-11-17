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
            var runtime = new PluginRuntime(pages, diagnostics: diagnostics);
            HotConfiguration? hotConfiguration = null;
            IDisposable? settings = null;
            IDisposable? manager = null;
            IDisposable? diagnosticsPage = null;
            desktop.MainWindow = new ShellWindow(pages);
            var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                var directory = Path.Combine(workspace, "profiles", "default");
                try
                {
                    DefaultProfile.Ensure(directory);
                    var catalog = DefaultProfile.Catalog(workspace);
                    var pluginDirectory = Path.Combine(workspace, "plugins");
                    var packages = new PluginPackages(pluginDirectory, catalog);
                    var packageErrors = packages.Discover();
                    var core = new CoreProfile(runtime, catalog, Path.Combine(directory, "core.json"));
                    await core.Start();
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
                }
                catch (Exception error) { DefaultProfile.Recovery(pages, error.Message, directory); }
                finally { initialized.TrySetResult(); }
            });
            var closing = false;
            var readyToClose = false;
            desktop.MainWindow.Closing += async (_, args) =>
            {
                if (readyToClose) return;
                args.Cancel = true;
                if (closing) return;
                closing = true;
                try
                {
                    await initialized.Task;
                    if (hotConfiguration is not null) await hotConfiguration.DisposeAsync();
                    settings?.Dispose(); manager?.Dispose(); diagnosticsPage?.Dispose();
                    await runtime.DisposeAsync();
                    readyToClose = true; desktop.MainWindow.Close();
                }
                catch (Exception error) { pages.Notice = "关闭未完成：" + error.Message; closing = false; }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
