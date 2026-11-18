using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class UnifiedInboxView : UserControl, IDisposable
{
    private sealed record Filter(Guid? Id, string Name) { public override string ToString() => Name; }
    private readonly IUnifiedInbox _service;
    private readonly CancellationTokenSource _stop = new();
    private readonly ComboBox _filter = new();
    private TextBlock? _rangeLabel;
    private readonly ComboBox _range = new() { ItemsSource = new[] { 50, 200, 500 }, SelectedIndex = 0 };
    private readonly TextBlock _states = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _body = new() { IsReadOnly = true, AcceptsReturn = true, MaxHeight = 230, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new() { MinHeight = 120, MaxHeight = 230 };
    private readonly Button _sync, _toggle, _prev, _next, _open;
    private readonly Stack<InboxPosition?> _back = new();
    private InboxPosition? _before;
    private IReadOnlyList<ReceiveStatus> _rows = [];
    private UnifiedPage _page = new([], false);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _settingFilter, _checking;
    private int _revision, _reading;
    private Guid? Account => (_filter.SelectedItem as Filter)?.Id;

    public UnifiedInboxView(IUnifiedInbox service)
    {
        _service = service;
        var panel = new StackPanel { Spacing = 8, MaxWidth = 900 };
        panel.Children.Add(new TextBlock { Text = "统一收件箱", FontSize = 28, FontWeight = FontWeight.SemiBold });
        void Field(string label, Control input, string id)
        {
            var caption = new TextBlock { Text = label }; panel.Children.Add(caption);
            if (input == _range) _rangeLabel = caption;
            AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label); panel.Children.Add(input);
        }
        Field("查看账户", _filter, "unified-filter");
        _list.ItemTemplate = new FuncDataTemplate<InboxMessage>((m, _) => new TextBlock
        {
            Text = m is null ? "" : $"{m.Subject}\n{m.AccountName} · {m.Sender} · {(m.Cached ? "可离线阅读" : "仅摘要")}", TextWrapping = TextWrapping.Wrap
        });
        var actions = new WrapPanel();
        Button Action(Panel holder, string title, string id, Func<Task> action)
        {
            var button = new Button { Content = title, Margin = new Avalonia.Thickness(0, 0, 6, 0) };
            AutomationProperties.SetAutomationId(button, id); button.Click += async (_, _) => await Guard(action);
            holder.Children.Add(button); return button;
        }
        _sync = Action(actions, "立即接收", "unified-sync", Refresh);
        _toggle = Action(actions, "开启自动接收", "unified-auto", Configure);
        panel.Children.Add(actions);
        Field("每个 IMAP 收件箱最近封数／每批 POP3 数量", _range, "unified-range");
        AutomationProperties.SetAutomationId(_states, "unified-status"); panel.Children.Add(_states);
        Field("已接收邮件", _list, "unified-list");
        var nav = new WrapPanel();
        _prev = Action(nav, "上一页", "unified-prev", async () => { _before = _back.Pop(); await Page(); });
        _next = Action(nav, "下一页", "unified-next", async () =>
        {
            if (!_page.HasMore) return;
            _back.Push(_before); _before = _page.Messages[^1].Position; await Page();
        });
        _open = Action(nav, "阅读", "unified-open", Open); panel.Children.Add(nav);
        AutomationProperties.SetAutomationId(_feedback, "unified-feedback"); panel.Children.Add(_feedback);
        AutomationProperties.SetAutomationId(_body, "unified-body"); panel.Children.Add(_body);
        Content = new ScrollViewer { Content = panel };
        _filter.SelectionChanged += async (_, _) =>
        {
            if (_settingFilter) return;
            _back.Clear(); _before = null; DisplayStates(true); await Guard(Page);
        };
        _range.SelectionChanged += async (_, _) =>
        {
            if (_range.SelectedItem is int range && _rows.FirstOrDefault(r => r.Account == Account) is { Available: true } row && row.Range != range)
                await Guard(async () => { await _service.Configure(row.Account, row.Automatic, range, _stop.Token); await Update(false); });
        };
        _list.SelectionChanged += (_, _) => { _reading++; _body.Text = ""; Buttons(); };
        _timer.Tick += async (_, _) => await Guard(() => Update(false));
        AttachedToVisualTree += async (_, _) => { await Guard(() => Update(true)); if (!_stop.IsCancellationRequested) _timer.Start(); };
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) _feedback.Text = e.Message; }
    }

    private async Task Update(bool force)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var rows = await _service.Status(_stop.Token);
            if (_stop.IsCancellationRequested) return;
            var filters = rows.Select(r => new Filter(r.Account, r.Name)).Prepend(new(null, "全部账户")).ToArray();
            bool filterChanged = _filter.ItemsSource is not Filter[] current || !current.SequenceEqual(filters);
            var changedInbox = !_rows.Select(r => (r.Account, r.LastSuccess)).SequenceEqual(rows.Select(r => (r.Account, r.LastSuccess)));
            _rows = rows;
            if (filterChanged)
            {
                var id = Account; _settingFilter = true;
                try { _filter.ItemsSource = filters; _filter.SelectedItem = filters.FirstOrDefault(f => f.Id == id) ?? filters[0]; }
                finally { _settingFilter = false; }
            }
            DisplayStates(filterChanged);
            if (force || filterChanged || (changedInbox && _before is null)) await Page();
        }
        finally { _checking = false; }
    }

    private void DisplayStates(bool resetRange)
    {
        var selected = _rows.FirstOrDefault(r => r.Account == Account);
        var visible = selected is null ? _rows : [selected];
        _states.Text = visible.Count == 0 ? "请先在账户管理添加收信账户。" : string.Join("\n", visible.Select(r =>
        {
            var state = r.State switch
            {
                "running" => "接收中", "queued" => "已排队", "offline" => "离线", "auth" => "需要重新授权",
                "ready" => "接收完成", "failed" => "接收失败", "unavailable" => "不可接收", _ => "已暂停"
            };
            return $"{r.Name} · {state} · 最近成功 {r.LastSuccess?.LocalDateTime.ToString("g") ?? "无"}　{r.Detail}";
        }));
        _toggle.IsVisible = _range.IsVisible = selected?.Available == true;
        _rangeLabel!.IsVisible = _range.IsVisible;
        _toggle.Content = selected?.Automatic == true ? "暂停自动接收" : "开启自动接收";
        if (resetRange && selected is not null) _range.SelectedItem = selected.Range;
        _sync.IsEnabled = visible.Any(r => r.Available);
    }

    private async Task Refresh()
    {
        foreach (var row in _rows.Where(r => r.Available && (Account is null || r.Account == Account)))
            await _service.Refresh(row.Account, _stop.Token);
        _feedback.Text = "已请求后台接收。"; await Update(false);
    }

    private async Task Configure()
    {
        var selected = _rows.Single(r => r.Account == Account);
        await _service.Configure(selected.Account, !selected.Automatic, _range.SelectedItem as int? ?? 50, _stop.Token);
        await Update(false);
    }

    private async Task Page()
    {
        var revision = ++_revision;
        var page = await _service.Inbox(Account, _before, 25, _stop.Token);
        if (_stop.IsCancellationRequested || revision != _revision) return;
        var selected = _list.SelectedItem as InboxMessage;
        var text = _body.Text;
        _page = page; _list.ItemsSource = page.Messages;
        _list.SelectedItem = page.Messages.FirstOrDefault(m => m.Account == selected?.Account && m.Position == selected.Position);
        if (_list.SelectedItem is not null) _body.Text = text;
        _feedback.Text = page.Messages.Count == 0 ? "此范围还没有已接收邮件。" : $"第 {_back.Count + 1} 页 · {page.Messages.Count} 封";
        Buttons();
    }

    private void Buttons()
    {
        _prev.IsEnabled = _back.Count > 0; _next.IsEnabled = _page.HasMore; _open.IsEnabled = _list.SelectedItem is InboxMessage;
        _open.Content = _list.SelectedItem is InboxMessage { Cached: false } ? "下载并阅读" : "离线阅读";
    }

    private async Task Open()
    {
        if (_list.SelectedItem is not InboxMessage item) return;
        var revision = ++_reading;
        _feedback.Text = item.Cached ? "正在读取本地副本…" : "正在下载邮件…";
        var text = await _service.Open(item.Account, item.Position.Key, _stop.Token);
        if (_stop.IsCancellationRequested || revision != _reading) return;
        _body.Text = text; _feedback.Text = item.Subject;
    }

    public void Dispose() { _timer.Stop(); _stop.Cancel(); _stop.Dispose(); }
}
