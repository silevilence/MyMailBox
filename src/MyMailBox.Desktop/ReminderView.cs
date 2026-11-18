using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class ReminderView : UserControl, IDisposable
{
    private readonly ReminderPage _page;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ListBox _list = new() { MinHeight = 170, MaxHeight = 260 };
    private readonly ComboBox _zone = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, _mode = new() { ItemsSource = new[] { "应用内横幅和提醒列表", "仅提醒列表（静默）" }, SelectedIndex = 0 };
    private readonly ComboBox _folders = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, _messages = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _title = new(), _time = new() { Text = DateTime.Now.AddMinutes(5).ToString("yyyy-MM-dd HH:mm:ss") };
    private readonly CheckBox _link = new() { Content = "关联下面选定的邮件" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap }, _mailStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _panel = new() { Spacing = 10 };
    private bool _disposed, _busy, _refreshing;
    private int _offset, _mailRevision;
    private bool _more;
    public ReminderView(ReminderPage page,Guid? linked=null)
    {
        _page = page;
        _panel.Children.Add(new TextBlock { Text = "提醒与计划", FontSize = 28, FontWeight = FontWeight.SemiBold });
        _panel.Children.Add(new TextBlock { Text = "应用运行且提醒插件启用时检查到期事项；完全退出或关机期间不会通知，下次启动补提醒一次。计划保存指定时区，主机时区改变不会改动到期瞬间。", TextWrapping = TextWrapping.Wrap });
        _list.ItemTemplate = new FuncDataTemplate<ReminderInfo>((r, _) => new TextBlock { Text = r is null ? "" : $"{State(r.State)} · {r.Title}\n{TimeZoneInfo.ConvertTime(r.DueUtc, TimeZoneInfo.FindSystemTimeZoneById(r.TimeZoneId)):yyyy-MM-dd HH:mm:ss zzz} · {r.TimeZoneId}\n{r.MessageState}" + (r.Error.Length == 0 ? "" : "\n投递未完成：" + r.Error), TextWrapping = TextWrapping.Wrap });
        Add("已保存的提醒", _list, "reminder-list");
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Action("延后 5 分钟", "reminder-snooze", async () => { await page.Scheduler.Snooze(Selected().Id, TimeSpan.FromMinutes(5), _lifetime.Token); await Refresh(); _status.Text = "已延后 5 分钟，到时重新提醒。"; }));
        actions.Children.Add(Action("关闭这次提醒", "reminder-dismiss", async () => { await page.Scheduler.Dismiss(Selected().Id, _lifetime.Token); await Refresh(); _status.Text = "提醒已关闭，不会再次弹出。"; }));
        actions.Children.Add(Action("取消计划", "reminder-cancel", async () => { await page.Scheduler.Cancel(Selected().Id, _lifetime.Token); await Refresh(); _status.Text = "计划已取消，记录保留。"; }));
        actions.Children.Add(Action("刷新", "reminder-refresh", Refresh)); _panel.Children.Add(actions);
        Add("提醒内容", _title, "reminder-title"); Add("计划时间（yyyy-MM-dd HH:mm，可加 :ss）", _time, "reminder-time");
        _zone.ItemsSource = TimeZoneInfo.GetSystemTimeZones(); _zone.ItemTemplate = new FuncDataTemplate<TimeZoneInfo>((z, _) => new TextBlock { Text = z?.DisplayName });
        _zone.SelectedItem = ((IEnumerable<TimeZoneInfo>)_zone.ItemsSource).FirstOrDefault(z => z.Id == TimeZoneInfo.Local.Id);
        Add("以上时间所属时区", _zone, "reminder-zone"); Add("通知方式", _mode, "reminder-mode");
        var messagePanel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(_link, "reminder-link"); messagePanel.Children.Add(_link);
        _folders.ItemTemplate = new FuncDataTemplate<MailFolder>((f, _) => new TextBlock { Text = f?.Path });
        _messages.ItemTemplate = new FuncDataTemplate<MailSummary>((m, _) => new TextBlock { Text = m is null ? "" : m.Subject + " · " + m.From });
        messagePanel.Children.Add(_folders); messagePanel.Children.Add(_messages);
        AutomationProperties.SetAutomationId(_folders, "reminder-folders"); AutomationProperties.SetName(_folders, "关联邮件目录");
        AutomationProperties.SetAutomationId(_messages, "reminder-messages"); AutomationProperties.SetName(_messages, "关联邮件");
        var paging = new WrapPanel { Orientation = Orientation.Horizontal };
        paging.Children.Add(Action("上一页邮件", "reminder-mail-previous", () => LoadMessages(Math.Max(0, _offset - 50))));
        paging.Children.Add(Action("下一页邮件", "reminder-mail-next", () => _more ? LoadMessages(_offset + 50) : Task.CompletedTask)); messagePanel.Children.Add(paging); messagePanel.Children.Add(_mailStatus);
        _panel.Children.Add(new Expander { Header = "关联邮件（可选）", Content = messagePanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        _panel.Children.Add(Action("保存一次性提醒", "reminder-create", async () =>
        {
            if (!DateTime.TryParseExact(_time.Text, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) throw new InvalidDataException("时间格式应为 yyyy-MM-dd HH:mm 或 yyyy-MM-dd HH:mm:ss。");
            if (_zone.SelectedItem is not TimeZoneInfo zone) throw new InvalidDataException("请选择时区。");
            Guid? message = null; if (_link.IsChecked == true) message = (_messages.SelectedItem as MailSummary)?.Id ?? throw new InvalidDataException("请选择关联邮件，或取消勾选关联。");
            var reminder = await page.Scheduler.Create(new(_title.Text ?? "", local, zone.Id, message, _mode.SelectedIndex == 1 ? ReminderNotificationMode.ListOnly : ReminderNotificationMode.Banner), _lifetime.Token);
            await Refresh(); _list.SelectedItem = (_list.ItemsSource as IEnumerable<ReminderInfo>)?.FirstOrDefault(r => r.Id == reminder.Id);
            _status.Text = $"提醒已保存：{reminder.DueUtc:yyyy-MM-dd HH:mm:ss} UTC。应用未运行时不会执行；已过期的时刻会在下一次检查补提醒。";
        }));
        AutomationProperties.SetAutomationId(_status, "reminder-status");
        var layout = new DockPanel(); DockPanel.SetDock(_status, Dock.Bottom); layout.Children.Add(_status); layout.Children.Add(new ScrollViewer { Content = _panel }); Content = layout;
        _folders.SelectionChanged += async (_, _) => await Guard(() => LoadMessages(0), false);
        _timer.Tick += async (_, _) => await Guard(Refresh, false);
        AttachedToVisualTree += async (_, _) => await Guard(async () => { await Refresh(); if(linked.HasValue)_list.SelectedItem=_list.Items.OfType<ReminderInfo>().FirstOrDefault(r=>r.Id==linked); var folders = await page.Mail.Folders(_lifetime.Token); if (!_disposed) { _folders.ItemsSource = folders; _folders.SelectedIndex = folders.Count > 0 ? 0 : -1; _timer.Start(); } });
    }
    private static string State(string state) => state switch { "pending" => "待提醒", "notified" => "已到期", "dismissed" => "已关闭", "canceled" => "已取消", _ => state };
    private ReminderInfo Selected() => _list.SelectedItem as ReminderInfo ?? throw new InvalidDataException("请先选择提醒。");
    private void Add(string label, Control input, string id) { _panel.Children.Add(new TextBlock { Text = label }); _panel.Children.Add(input); AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label); }
    private Button Action(string text, string id, Func<Task> action)
    { var button = new Button { Content = text }; AutomationProperties.SetAutomationId(button, id); button.Click += async (_, _) => await Guard(action); return button; }
    private async Task Guard(Func<Task> action, bool lockForm = true)
    {
        if (_disposed || lockForm && _busy) return;
        if (lockForm) { _busy = true; _panel.IsEnabled = false; }
        try { await action(); } catch (OperationCanceledException) { } catch (Exception e) { if (!_disposed) _status.Text = "操作未完成：" + e.Message; }
        finally { if (lockForm) { _busy = false; _panel.IsEnabled = true; } }
    }
    private async Task Refresh()
    {
        if (_refreshing) return; _refreshing = true;
        try
        {
            var id = (_list.SelectedItem as ReminderInfo)?.Id; var items = await _page.Scheduler.List(_lifetime.Token); if (_disposed) return;
            _list.ItemsSource = items; _list.SelectedItem = items.FirstOrDefault(r => r.Id == id) ?? items.FirstOrDefault();
        }
        finally { _refreshing = false; }
    }
    private async Task LoadMessages(int offset)
    {
        var revision = ++_mailRevision; if (_folders.SelectedItem is not MailFolder folder) return;
        var result = await _page.Mail.List(folder.Id, offset, 50, _lifetime.Token); if (_disposed || revision != _mailRevision) return;
        _offset = offset; _more = result.HasMore; _messages.ItemsSource = result.Items; _messages.SelectedIndex = result.Items.Count > 0 ? 0 : -1; _mailStatus.Text = $"第 {offset / 50 + 1} 页，{result.Items.Count} 封" + (_more ? "，可继续下一页" : "");
    }
    public void Dispose() { _disposed = true; _timer.Stop(); _lifetime.Cancel(); _lifetime.Dispose(); }
}
