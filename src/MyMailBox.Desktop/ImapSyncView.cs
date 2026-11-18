using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class ImapSyncView : StackPanel, IDisposable
{
    private readonly IImapSyncService _service;
    private readonly Guid _account;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _folder = new();
    private readonly ComboBox _scope = new() { ItemsSource = new[] { "最近 50 封", "最近 200 封", "最近 1000 封", "最近 5000 封" }, SelectedIndex = 0 };
    private readonly ListBox _list = new() { MinHeight = 110, MaxHeight = 220 };
    private readonly TextBox _body = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200, AcceptsReturn = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _readStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _start, _watch, _stop, _previous, _next, _open;
    private CancellationTokenSource? _sync;
    private int _offset, _pageVersion, _readVersion;
    private bool _hasMore, _refreshing;
    private string Path => _folder.SelectedItem as string ?? "INBOX";

    public ImapSyncView(IImapSyncService service, Guid account)
    {
        _service = service;
        _account = account;
        Spacing = 8;
        void Field(string label, Control control, string id)
        {
            Children.Add(new TextBlock { Text = label });
            AutomationProperties.SetName(control, label);
            AutomationProperties.SetAutomationId(control, id);
            Children.Add(control);
        }
        Field("同步目录", _folder, "sync-folder");
        Field("每次检查的范围", _scope, "sync-scope");
        var actions = new WrapPanel();
        Button Action(Panel parent, string label, string id, Func<Task> action)
        {
            var button = new Button { Content = label, Margin = new Avalonia.Thickness(0, 0, 6, 4) };
            AutomationProperties.SetAutomationId(button, id);
            button.Click += async (_, _) => await Guard(action);
            parent.Children.Add(button);
            return button;
        }
        _start = Action(actions, "立即同步", "sync-once", () => Sync(false));
        _watch = Action(actions, "持续接收", "sync-watch", () => Sync(true));
        _stop = Action(actions, "停止", "sync-stop", () => { _sync?.Cancel(); return Task.CompletedTask; });
        Children.Add(actions);
        AutomationProperties.SetAutomationId(_status, "sync-status");
        Children.Add(_status);
        _list.ItemTemplate = new FuncDataTemplate<ImapCachedMail>((m, _) => new TextBlock
        {
            Text = m is null ? "" : $"{m.Subject}\n{m.Sender} · {(m.LocalId.HasValue ? "可离线阅读" : m.Missing ? "服务器已移除" : "仅摘要")}",
            TextWrapping = TextWrapping.Wrap
        });
        Field("已同步邮件", _list, "sync-messages");
        var reading = new WrapPanel();
        _previous = Action(reading, "上一页", "sync-prev", () => LoadPage(_offset - 20));
        _next = Action(reading, "下一页", "sync-next", () => LoadPage(_offset + 20));
        _open = Action(reading, "打开邮件", "sync-open", Open);
        Children.Add(reading);
        Children.Add(_readStatus);
        AutomationProperties.SetAutomationId(_body, "sync-body");
        Children.Add(_body);
        _folder.SelectionChanged += async (_, _) =>
        {
            ClearReading();
            await Guard(() => LoadPage(0));
        };
        _list.SelectionChanged += (_, _) => { if (!_refreshing) ClearReading(); Buttons(); };
        Buttons();
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_lifetime.IsCancellationRequested) _status.Text = e.Message; }
    }

    public void SetFolders(IEnumerable<string> paths)
    {
        var selected = Path;
        var items = paths.Prepend("INBOX").Distinct(StringComparer.Ordinal).ToArray();
        _folder.ItemsSource = items;
        _folder.SelectedItem = items.Contains(selected) ? selected : items[0];
    }

    private void Buttons()
    {
        _start.IsEnabled = _watch.IsEnabled = _folder.IsEnabled = _scope.IsEnabled = _sync is null;
        _stop.IsEnabled = _sync is not null;
        _previous.IsEnabled = _offset > 0;
        _next.IsEnabled = _hasMore;
        _open.IsEnabled = _list.SelectedItem is ImapCachedMail { Missing: false };
        _open.Content = _list.SelectedItem is ImapCachedMail { LocalId: null, Missing: false } ? "下载正文和附件" : "离线打开";
    }

    private async Task LoadPage(int offset)
    {
        var version = ++_pageVersion;
        var page = await _service.CachedPage(_account, Path, offset, 20, _lifetime.Token);
        if (_lifetime.IsCancellationRequested || version != _pageVersion) return;
        var selected = _list.SelectedItem as ImapCachedMail;
        var current = page.Items.FirstOrDefault(m => m.FolderId == selected?.FolderId && m.Uid == selected.Uid);
        // Keep the reader open when a refresh only changes the cache badge.
        _refreshing = true;
        try { _list.ItemsSource = page.Items; _list.SelectedItem = current; }
        finally { _refreshing = false; }
        if (current is null) ClearReading();
        _offset = offset; _hasMore = page.HasMore;
        Buttons();
    }

    private void ClearReading()
    {
        _readVersion++;
        _body.Text = "";
        _readStatus.Text = "";
    }

    private async Task Sync(bool watch)
    {
        if (_sync is not null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sync = operation;
        Buttons();
        var path = Path;
        var limit = new[] { 50, 200, 1000, 5000 }[_scope.SelectedIndex];
        try
        {
            do
            {
                _status.Text = "正在接收摘要…";
                bool firstBatch = true;
                var progress = new Progress<int>(n =>
                {
                    if (_sync != operation || operation.IsCancellationRequested) return;
                    _status.Text = $"正在接收摘要，本次新增 {n} 封。";
                    if (firstBatch) { firstBatch = false; _ = Guard(() => LoadPage(_offset)); }
                });
                var result = await _service.Sync(_account, path, limit, progress, operation.Token);
                await LoadPage(_offset);
                _status.Text = $"同步完成，新增 {result.Added} 封。" + (watch ? " 正在等待新邮件…" : "");
                if (watch) await _service.WaitForChange(_account, path, result, operation.Token);
            } while (watch && !operation.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_lifetime.IsCancellationRequested) { await LoadPage(_offset); _status.Text = "同步已停止，已接收的摘要已保存。"; }
        }
        finally { _sync = null; if (!_lifetime.IsCancellationRequested) Buttons(); }
    }

    private async Task Open()
    {
        if (_list.SelectedItem is not ImapCachedMail message) return;
        int version = ++_readVersion;
        _readStatus.Text = message.LocalId is null ? "正在下载…" : "正在读取本地副本…";
        try
        {
            var result = await _service.OpenMessage(_account, Path, message, message.LocalId is null, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || version != _readVersion) return;
            _body.Text = result.Text;
            _readStatus.Text = $"{result.Subject} · 已保存。可在本地邮箱的对应目录查看安全 HTML 和附件。";
            await LoadPage(_offset);
        }
        catch (Exception e) when (!_lifetime.IsCancellationRequested && version == _readVersion)
        {
            _readStatus.Text = e.Message;
            await LoadPage(_offset);
        }
    }

    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
