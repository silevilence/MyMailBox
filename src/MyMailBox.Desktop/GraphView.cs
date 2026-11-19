using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class GraphView : UserControl, IDisposable
{
    private readonly GraphPage _page;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private readonly ComboBox _accounts = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _folders = new() { Height = 150 }, _messages = new() { Height = 220 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Control> _controls = [];
    private string? _parent, _folderNext, _messageNext, _messageCurrent;
    private readonly Stack<string?> _parents = new(), _messageBack = new();
    private List<GraphFolder> _folderItems = [];
    private readonly ComboBox _syncScopes = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _offline = new() { Height = 150 };
    private int _offlineOffset;
    private bool _offlineMore;
    private bool _busy, _disposed;
    private Guid Account => (_accounts.SelectedItem as MailAccount)?.Id ?? throw new InvalidDataException("请先在账户管理添加 Outlook.com 个人账户。");
    public GraphView(GraphPage page)
    {
        _page = page;
        _accounts.ItemTemplate = new FuncDataTemplate<MailAccount>((a, _) => new TextBlock { Text = a?.Name + " · " + a?.Address });
        _folders.ItemTemplate = new FuncDataTemplate<GraphFolder>((f, _) => new TextBlock { Text = f is null ? "" : f.Name + (f.ChildCount > 0 ? $"（{f.ChildCount} 个子目录）" : "") });
        _messages.ItemTemplate = new FuncDataTemplate<GraphSummary>((m, _) => new TextBlock { Text = m is null ? "" : $"{(m.IsRead ? "" : "未读 · ")}{m.Subject} · {m.From}{(m.HasAttachments ? " · 附件" : "")}", TextWrapping = TextWrapping.Wrap });
        var panel = new StackPanel { Spacing = 10, MaxWidth = 850 };
        panel.Children.Add(new TextBlock { Text = "Outlook.com 邮件", FontSize = 28 });
        panel.Children.Add(new TextBlock { Text = "个人微软账户 · 只读授权。浏览和下载不会改变云端已读状态。下载后到“本地邮件”查看正文、原文与附件。", TextWrapping = TextWrapping.Wrap });
        Add(panel, "账户", _accounts, "graph-account");
        var connection = Row(); panel.Children.Add(connection);
        connection.Children.Add(Button("登录微软账户", "graph-login", async t => { _status.Text = "请在微软官方浏览器页面完成授权；可随时取消。"; await page.Service.SignIn(Account, t); _status.Text = "微软只读授权已完成，可以读取目录。"; }));
        connection.Children.Add(Button("断开本应用授权", "graph-disconnect", async t => { await page.Service.Disconnect(Account, t); ClearLists(); _status.Text = "已清除本应用的登录缓存；已下载邮件保留。微软账户及 Outlook 登录不受影响。"; }));
        var navigation = Row(); panel.Children.Add(navigation);
        navigation.Children.Add(Button("读取根目录", "graph-root", async t => { _parents.Clear(); _parent = null; await LoadFolders(null, t); }));
        navigation.Children.Add(Button("进入子目录", "graph-children", async t => { var folder = _folders.SelectedItem as GraphFolder ?? throw new InvalidDataException("请选择目录。"); if (folder.ChildCount == 0) throw new InvalidDataException("此目录没有子目录。"); _parents.Push(_parent); _parent = folder.Id; await LoadFolders(null, t); }));
        navigation.Children.Add(Button("返回上层", "graph-up", async t => { if (_parents.Count == 0) throw new InvalidDataException("已在根目录。"); _parent = _parents.Pop(); await LoadFolders(null, t); }));
        navigation.Children.Add(Button("更多目录", "graph-more-folders", async t => { if (_folderNext is null) throw new InvalidDataException("没有更多目录。"); await LoadFolders(_folderNext, t); }));
        Add(panel, "云端目录", _folders, "graph-folders");
        var paging = Row(); panel.Children.Add(paging);
        paging.Children.Add(Button("读取邮件摘要", "graph-read", async t => { _messageBack.Clear(); await LoadMessages(null, t); }));
        paging.Children.Add(Button("上一页", "graph-previous", async t => { if (_messageBack.Count == 0) throw new InvalidDataException("已在第一页。"); var previous = _messageBack.Peek(); await LoadMessages(previous, t); _messageBack.Pop(); }));
        paging.Children.Add(Button("下一页", "graph-next", async t => { if (_messageNext is null) throw new InvalidDataException("没有下一页。"); var current = _messageCurrent; await LoadMessages(_messageNext, t); _messageBack.Push(current); }));
        Add(panel, "邮件摘要", _messages, "graph-messages");
        panel.Children.Add(Button("下载所选邮件到本地阅读", "graph-download", async t => { var message = _messages.SelectedItem as GraphSummary ?? throw new InvalidDataException("请选择邮件。"); await page.Service.Download(Account, message.Id, t); _status.Text = "原文和附件已保存。打开“本地邮件”，选择当前账户的“Outlook.com 已下载”目录阅读。云端状态未更改。"; }));
        if (page.Sync is { } sync)
        {
            panel.Children.Add(Button("增量同步选中目录的摘要", "graph-sync", async t =>
            {
                var folder = _folders.SelectedItem as GraphFolder ?? throw new InvalidDataException("请先选择云端目录。");
                _status.Text = $"正在同步 {folder.Name}，可取消并在下次继续。";
                var scope = await sync.Sync(Account, folder, t);
                await LoadScopes(sync, scope.Id, t);
                _status.Text = $"{scope.Name} 同步完成，共 {scope.Messages} 封，{scope.LastSuccess?.ToLocalTime():g}。";
            }));
            _syncScopes.ItemTemplate = new FuncDataTemplate<GraphSyncScope>((s, _) => new TextBlock { Text = s == null ? "" : $"{s.Name} · {s.Messages} 封 · 上次成功 {s.LastSuccess?.ToLocalTime().ToString("g") ?? "尚未完成"}" });
            Add(panel, "已同步的目录", _syncScopes, "graph-offline-scope");
            _offline.ItemTemplate = _messages.ItemTemplate;
            Add(panel, "本地摘要", _offline, "graph-offline-messages");
            var cached = Row(); panel.Children.Add(cached);
            _syncScopes.SelectionChanged += (_, _) => { _offlineOffset = 0; _offlineMore = false; _offline.ItemsSource = null; };
            cached.Children.Add(Button("查看本地摘要", "graph-offline", t => LoadOffline(sync, 0, t)));
            cached.Children.Add(Button("上一页摘要", "graph-offline-prev", t => LoadOffline(sync, Math.Max(0, _offlineOffset - 25), t)));
            cached.Children.Add(Button("下一页摘要", "graph-offline-next", t => LoadOffline(sync, _offlineMore ? _offlineOffset + 25 : _offlineOffset, t)));
            cached.Children.Add(Button("保存选中邮件供离线阅读", "graph-offline-download", async t =>
            {
                if (_offline.SelectedItem is not GraphSummary message) throw new InvalidDataException("请选择一封本地摘要。");
                await page.Service.Download(Account, message.Id, t);
                _status.Text = "正文与附件已保存在本地邮箱。";
            }));
        }
        var feedback = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(_status, "graph-status"); feedback.Children.Add(_status);
        var cancel = new Button { Content = "取消当前操作" }; AutomationProperties.SetAutomationId(cancel, "graph-cancel"); cancel.Click += (_, _) => _operation?.Cancel(); feedback.Children.Add(cancel);
        var layout = new DockPanel(); DockPanel.SetDock(feedback, Dock.Bottom); layout.Children.Add(feedback); layout.Children.Add(new ScrollViewer { Content = panel }); Content = layout;
        _accounts.SelectionChanged += async (_, _) => { if (!_busy) await Run(LoadSettings); };
        _folders.SelectionChanged += (_, _) => { _messages.ItemsSource = null; _messageNext = null; _messageCurrent = null; _messageBack.Clear(); };
        AttachedToVisualTree += async (_, _) => await Run(async t => { _accounts.ItemsSource = (await page.Accounts.List(t)).Where(a => a.Provider == "outlook-com" && a.Enabled).ToArray(); _accounts.SelectedIndex = 0; await LoadSettings(t); });
    }
    private static WrapPanel Row() => new() { Orientation = Orientation.Horizontal };
    private void Add(StackPanel panel, string label, Control control, string id)
    {
        panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(control); AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, label); _controls.Add(control);
    }
    private Button Button(string title, string id, Func<CancellationToken, Task> action)
    {
        var button = new Button { Content = title, Margin = new Avalonia.Thickness(0, 0, 8, 6) }; AutomationProperties.SetAutomationId(button, id); button.Click += async (_, _) => await Run(action); _controls.Add(button); return button;
    }
    private async Task LoadSettings(CancellationToken token)
    {
        ClearLists();
        if (_accounts.SelectedItem is null) { _status.Text = "请先在账户管理添加 Outlook.com（个人账户，只读）。"; return; }
        var settings = await _page.Service.Settings(Account, token);
        if (settings.SignedIn && _page.Sync is { } sync) await LoadScopes(sync, null, token);
        _status.Text = settings.SignedIn ? "已登录，可以读取目录。" : "请选择对应账户并点击“登录微软账户”，在微软页面完成授权。";
    }
    private async Task LoadScopes(IGraphFolderSync sync, string? selected, CancellationToken token)
    {
        var scopes = await sync.Scopes(Account, token);
        _syncScopes.ItemsSource = scopes; _syncScopes.SelectedItem = scopes.FirstOrDefault(s => s.Id == selected) ?? scopes.FirstOrDefault();
    }
    private async Task LoadOffline(IGraphFolderSync sync, int offset, CancellationToken token)
    {
        if (_syncScopes.SelectedItem is not GraphSyncScope scope) throw new InvalidDataException("请先同步一个云端目录。");
        var page = await sync.Cached(Account, scope.Id, offset, token);
        _offlineOffset = offset; _offlineMore = page.More; _offline.ItemsSource = page.Items; _offline.SelectedIndex = page.Items.Count > 0 ? 0 : -1;
        _status.Text = $"{scope.Name} · 本地第 {offset / 25 + 1} 页，共 {page.Items.Count} 条摘要。";
    }
    private void ClearLists() { _syncScopes.ItemsSource = null; _offline.ItemsSource = null; _folders.ItemsSource = null; _messages.ItemsSource = null; _folderItems = []; _parent = null; _parents.Clear(); _folderNext = null; _messageNext = null; _messageCurrent = null; _messageBack.Clear(); }
    private async Task LoadFolders(string? next, CancellationToken token)
    {
        var result = await _page.Service.Folders(Account, _parent, next, token); _folderItems = next is null ? result.Items.ToList() : [.. _folderItems, .. result.Items]; _folderNext = result.Next;
        _folders.ItemsSource = _folderItems; _folders.SelectedIndex = 0; _status.Text = $"已读取 {_folderItems.Count} 个目录。";
    }
    private async Task LoadMessages(string? next, CancellationToken token)
    {
        var folder = _folders.SelectedItem as GraphFolder ?? throw new InvalidDataException("请选择目录。");
        var result = await _page.Service.Messages(Account, folder.Id, next, token); _messages.ItemsSource = result.Items; _messages.SelectedIndex = 0; _messageNext = result.Next; _messageCurrent = next;
        _status.Text = $"已读取 {result.Items.Count} 封邮件摘要；云端已读状态未更改。";
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (_busy || _disposed) return; _busy = true; foreach (var control in _controls) control.IsEnabled = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "操作已取消，可以重试。"; }
        catch (Exception e) { if (!_disposed) _status.Text = e is InvalidDataException or InvalidOperationException ? e.Message : "操作未完成，请检查配置或稍后重试。"; }
        finally { _operation = null; _busy = false; if (!_disposed) foreach (var control in _controls) control.IsEnabled = true; }
    }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
