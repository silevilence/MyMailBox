using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class UpdateView : UserControl, IDisposable
{
    private readonly IUpdateService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _panel = new() { Spacing = 12, MaxWidth = 850, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _repository = new(), _interval = new() { Text = "6" };
    private readonly ComboBox _channel = new() { ItemsSource = new[] { "稳定版", "预览版及稳定版" }, SelectedIndex = 0 };
    private readonly CheckBox _background = new() { Content = "应用运行时在后台检查更新" };
    private readonly TextBlock _current = new(), _status = new() { TextWrapping = TextWrapping.Wrap }, _skipped = new(), _notes = new() { TextWrapping = TextWrapping.Wrap };
    private string? _skip, _available;
    private bool _disposed, _busy;
    public UpdateView(IUpdateService service)
    {
        _service = service;
        _panel.Children.Add(new TextBlock { Text = "软件更新", FontSize = 28, FontWeight = FontWeight.SemiBold }); _panel.Children.Add(_current);
        _panel.Children.Add(new TextBlock { Text = "从指定 GitHub 仓库检查公开发布。检查不会下载或安装；后台检查仅在应用及更新插件运行时执行。", TextWrapping = TextWrapping.Wrap });
        Add("更新仓库（owner/repo）", _repository, "update-repository"); Add("更新通道", _channel, "update-channel"); Add("后台检查", _background, "update-background"); Add("检查间隔（1–168 小时）", _interval, "update-interval");
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("保存设置", "update-save", async () =>
        {
            if (!int.TryParse(_interval.Text, out var hours)) throw new ArgumentException("请填写有效的检查间隔。");
            await service.Save(new(_repository.Text ?? "", (UpdateChannel)_channel.SelectedIndex, _background.IsChecked == true, hours, _skip), _lifetime.Token);
            await Refresh(); _status.Text = "设置已保存，下次检查使用新设置。";
        }));
        actions.Children.Add(Button("立即检查", "update-check", async () => { _status.Text = "正在检查…"; Show(await service.Check(true, _lifetime.Token)); }));
        actions.Children.Add(Button("跳过此版本", "update-skip", async () => { if (_available is null) throw new InvalidOperationException("请先检查到可用版本。"); await service.Skip(_available, _lifetime.Token); await Refresh(); }));
        actions.Children.Add(Button("取消跳过", "update-unskip", async () => { await service.Skip(null, _lifetime.Token); await Refresh(); }));
        _panel.Children.Add(actions); _panel.Children.Add(_skipped);
        AutomationProperties.SetAutomationId(_status, "update-status"); AutomationProperties.SetAutomationId(_notes, "update-notes");
        _panel.Children.Add(_status); _panel.Children.Add(new TextBlock { Text = "版本说明", FontWeight = FontWeight.SemiBold }); _panel.Children.Add(_notes);
        Content = new ScrollViewer { Content = _panel, Padding = new(24) }; AttachedToVisualTree += async (_, _) => await Guard(Refresh);
    }
    private async Task Refresh()
    {
        var state = await _service.Snapshot(_lifetime.Token); if (_disposed) return;
        _current.Text = "当前版本 V" + state.CurrentVersion; _repository.Text = state.Settings.Repository; _channel.SelectedIndex = (int)state.Settings.Channel;
        _background.IsChecked = state.Settings.Background; _interval.Text = state.Settings.IntervalHours.ToString(); _skip = state.Settings.SkippedVersion;
        _skipped.Text = _skip is null ? "没有跳过的版本。" : "后台检查已跳过 V" + _skip + "。"; Show(state.Last);
    }
    private void Show(UpdateCheck result)
    {
        if (_disposed) return; _available = result.Release?.Version;
        _status.Text = result.Message + (result.CheckedUtc == DateTimeOffset.MinValue ? "" : $"\n检查时间：{result.CheckedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}") + (result.Cached ? "（缓存结果，避免频繁请求）" : "") + (result.RetryAfterUtc is { } retry ? $"\n可重试时间：{retry.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "");
        _notes.Text = result.Release?.Notes ?? "暂无可用的版本说明。";
    }
    private void Add(string label, Control control, string id) { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, label); _panel.Children.Add(new TextBlock { Text = label }); _panel.Children.Add(control); }
    private Button Button(string label, string id, Func<Task> action)
    { var button = new Button { Content = label, Margin = new(0, 0, 8, 8) }; AutomationProperties.SetAutomationId(button, id); button.Click += async (_, _) => await Guard(action); return button; }
    private async Task Guard(Func<Task> action)
    {
        if (_disposed || _busy) return; _busy = true; _panel.IsEnabled = false;
        try { await action(); } catch (OperationCanceledException) { } catch (Exception e) { if (!_disposed) _status.Text = "操作未完成：" + e.Message; }
        finally { _busy = false; if (!_disposed) _panel.IsEnabled = true; }
    }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
