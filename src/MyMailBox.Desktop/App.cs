using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using CordiSharp;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
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
            var context = Context.Create();
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
            context.Provide<IContributions>("mailbox.ui", pages);
            desktop.MainWindow = new ShellWindow(pages);
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                var directory = Path.Combine(workspace, "profiles", "default");
                try
                {
                    DefaultProfile.Ensure(directory);
                    var catalog = DefaultProfile.Catalog();
                    var pluginDirectory = Path.Combine(workspace, "plugins");
                    var packages = new PluginPackages(pluginDirectory, catalog);
                    var packageErrors = packages.Discover();
                    var core = new CoreProfile(runtime, catalog, Path.Combine(directory, "core.json"));
                    await core.Start();
                    if (core.Error is not null) DefaultProfile.Recovery(pages, core.Error, directory);
                    else
                    {
                        hotConfiguration = new HotConfiguration(runtime, catalog, core.ActivePlugins, Path.Combine(directory, "plugins.json"));
                        settings = PluginSettings.Register(pages, hotConfiguration);
                        await hotConfiguration.Reload();
                        manager = PluginManager.Register(pages, packages, catalog, hotConfiguration, Path.Combine(directory, "core.json"), runtime);
                        diagnosticsPage = DiagnosticsPage.Register(pages, runtime, hotConfiguration, catalog, diagnostics, workspace);
                        if (packageErrors.Count > 0) pages.Notice = string.Join("\n", packageErrors);
                    }
                }
                catch (Exception error) { DefaultProfile.Recovery(pages, error.Message, directory); }
            });
            desktop.Exit += (_, _) =>
            {
                settings?.Dispose();
                manager?.Dispose();
                diagnosticsPage?.Dispose();
                hotConfiguration?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                context.Fiber.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
