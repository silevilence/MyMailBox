using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CordiSharp;
using CordiSharp.Registry;
using MyMailBox.Contracts;

namespace NativePlugin;

[Plugin("native")]
[Inject("mailbox.host")]
public sealed class AnimatedPlugin : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.AddNative(new("native", "高级控件验证", () => new AnimatedView(), view => ((AnimatedView)view).Dispose())));
    }
}

public sealed class AnimatedView : Border, IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    public AnimatedView()
    {
        Resources["sample-accent"] = Brushes.Teal;
        Background = (IBrush)Resources["sample-accent"]!;
        Child = new TextBlock { Text = "高级 Avalonia 控件 · 动画与资源释放验证", Foreground = Brushes.White, Margin = new Avalonia.Thickness(24) };
        Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) } };
        _timer.Tick += Tick; _timer.Start();
    }
    private void Tick(object? sender, EventArgs e) => Opacity = Opacity == 1 ? .6 : 1;
    public void Dispose()
    {
        _timer.Stop(); _timer.Tick -= Tick; Transitions?.Clear(); Transitions = null;
        Child = null; Resources.Clear(); Background = null;
    }
}
