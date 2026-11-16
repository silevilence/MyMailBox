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
            var pages = new Contributions();
            var runtime = new PluginRuntime(pages);
            context.Provide<IContributions>("mailbox.ui", pages);
            context.Inject(["mailbox.ui"], (ctx, _) => { WelcomePlugin.Apply(ctx); return null; }).Await().GetAwaiter().GetResult();
            context.Inject(["mailbox.ui"], (ctx, _) => { PluginLab.Apply(ctx, runtime); return null; }).Await().GetAwaiter().GetResult();
            desktop.MainWindow = new ShellWindow(pages);
            desktop.Exit += (_, _) =>
            {
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                context.Fiber.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
