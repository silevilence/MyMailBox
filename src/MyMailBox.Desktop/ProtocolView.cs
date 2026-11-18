using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed partial class ProtocolView : UserControl, IDisposable
{
    private readonly ProtocolPage _page;
    private readonly ComboBox _presetChoice = new();
    private readonly TextBlock _presetGuide = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _presetPanel = new() { Spacing = 6 };
    private IReadOnlyList<ProtocolPreset> _presets = [];
    private string? _presetId;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private readonly ComboBox _accounts = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly EndpointEditor _incoming = new("incoming"), _outgoing = new("outgoing");
    private readonly ComboBox _folders = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _messages = new() { MinHeight = 160, MaxHeight = 280 };
    private readonly StackPanel _imap = new() { Spacing = 8 };
    private readonly List<Button> _actions = [];
    private readonly List<Control> _editors = [];
    private readonly TabItem _syncTab = new() { Header = "离线同步" };
    private ImapSyncView? _syncView;
    private int _offset;
    private bool _hasMore, _busy, _disposed;
    private MailAccount? _selected;
    private IReadOnlyList<TabItem> _tabs = [];
    private Button _next = null!, _previous = null!;
    private readonly Action<ReplyIntent>? _respond;
    public ProtocolView(ProtocolPage page,Action<ReplyIntent>? respond=null)
    {
        _page = page; _respond=respond;
        var panel = new StackPanel { Spacing = 14, MaxWidth = 880 };
        panel.Children.Add(new TextBlock { Text = "邮箱连接与收发", FontSize = 28, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "先在账户管理添加在线账户，再配置服务器。保存配置不会连接或发送邮件。", TextWrapping = TextWrapping.Wrap });
        _accounts.ItemTemplate = new FuncDataTemplate<MailAccount>((a, _) => new TextBlock { Text = a is null ? "" : $"{a.Name} · {a.Address} · {a.Provider}" });
        Add(panel, "在线账户", _accounts, "protocol-account"); _editors.Add(_accounts);
        panel.Children.Add(Action("刷新账户", "protocol-refresh", LoadAccounts));
        var tabs = new TabControl(); var connection = new StackPanel { Spacing = 12 };
        _presetChoice.ItemTemplate = new FuncDataTemplate<Choice>((c,_) => new TextBlock { Text = c?.Label });
        Add(_presetPanel,"服务器预设",_presetChoice,"protocol-preset");
        _presetPanel.Children.Add(Action("应用所选预设","protocol-apply-preset",ApplyPreset));
        AutomationProperties.SetAutomationId(_presetGuide,"protocol-preset-guide");
        _presetPanel.Children.Add(_presetGuide);connection.Children.Add(_presetPanel);
        connection.Children.Add(_incoming.Panel); connection.Children.Add(_outgoing.Panel); _editors.Add(_incoming.Panel); _editors.Add(_outgoing.Panel);
        connection.Children.Add(Action("保存连接配置", "protocol-save", Save));
        _folders.ItemTemplate = new FuncDataTemplate<RemoteFolder>((f, _) => new TextBlock { Text = f is null ? "" : $"{f.Path} · {f.Role}" });
        _messages.ItemTemplate = new FuncDataTemplate<RemoteSummary>((m, _) => new TextBlock { Text = m is null ? "" : $"{m.Subject}\n{m.From} · {m.Date:g} · {m.Size:N0} 字节", TextWrapping = TextWrapping.Wrap });
        _imap.Children.Add(Action("连接并读取目录", "imap-connect", ConnectImap));
        Add(_imap, "服务器目录", _folders, "imap-folder"); _editors.Add(_folders);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        navigation.Children.Add(Action("读取／刷新摘要", "imap-read", () => ReadSummaries(0)));
        _previous = Action("上一页", "imap-previous", () => ReadSummaries(Math.Max(0, _offset - 20)));
        _next = Action("下一页", "imap-next", () => ReadSummaries(_offset + 20));
        navigation.Children.Add(_previous); navigation.Children.Add(_next); _imap.Children.Add(navigation);
        AutomationProperties.SetAutomationId(_messages, "imap-messages"); _imap.Children.Add(_messages);
        var items = new List<TabItem> { new() { Header = "连接设置", Content = connection }, new() { Header = "IMAP 目录", Content = _imap } };
        AddCompose(items); AddPop(items); if (_page.ImapSync is not null) items.Add(_syncTab); AddOutbox(items); _tabs = items; tabs.ItemsSource = items; AutomationProperties.SetAutomationId(tabs, "protocol-tabs"); panel.Children.Add(tabs);
        var cancel = new Button { Content = "取消当前操作" }; AutomationProperties.SetAutomationId(cancel, "protocol-cancel"); cancel.Click += (_, _) => _operation?.Cancel();
        AutomationProperties.SetAutomationId(_status, "protocol-status");
        var feedback = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 10, 0, 0) }; feedback.Children.Add(_status); feedback.Children.Add(cancel);
        var layout = new DockPanel(); DockPanel.SetDock(feedback, Dock.Bottom); layout.Children.Add(feedback); layout.Children.Add(new ScrollViewer { Content = panel }); Content = layout;
        _accounts.SelectionChanged += async (_, _) => { if (!_busy) await Run(SelectAccount); };
        _folders.SelectionChanged += (_, _) => { _offset = 0; _hasMore = false; _messages.ItemsSource = null; UpdateButtons(); };
        AttachedToVisualTree += async (_, _) => await Run(LoadAccounts);
    }
    partial void AddCompose(List<TabItem> items);
    partial void AddPop(List<TabItem> items);
    partial void UpdateCompose();
    partial void UpdatePop();
    private CancellationToken Token => _operation?.Token ?? _lifetime.Token;
    private static void Add(StackPanel panel, string label, Control input, string id)
    {
        AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label);
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Medium }); panel.Children.Add(input);
    }
    private Button Action(string text, string id, Func<Task> action)
    {
        var button = new Button { Content = text }; AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await Run(action); _actions.Add(button); return button;
    }
    private async Task Run(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true; _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); UpdateButtons(); _status.Text = "正在处理…";
        try { await action(); }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "操作已取消。"; }
        catch (Exception e) { if (!_disposed) _status.Text = "未完成：" + e.Message; }
        finally { _operation.Dispose(); _operation = null; _busy = false; if (!_disposed) UpdateButtons(); }
    }
    private void UpdateButtons()
    {
        foreach (var action in _actions) action.IsEnabled = !_busy;
        foreach (var editor in _editors) editor.IsEnabled = !_busy;
        if (_next is not null) _next.IsEnabled = !_busy && _hasMore;
        if (_previous is not null) _previous.IsEnabled = !_busy && _offset > 0;
        if (_tabs.Count > 1) _tabs[1].IsEnabled = _selected?.Capabilities.HasFlag(AccountCapabilities.Folders) == true;
        if (_tabs.Count > 2) _tabs[2].IsEnabled = _selected?.Capabilities.HasFlag(AccountCapabilities.Send) == true;
        if (_tabs.Count > 3) _tabs[3].IsEnabled = _selected?.Provider == "pop3";
        UpdateCompose(); UpdatePop(); OutboxButtons();
    }
    private async Task LoadAccounts()
    {
        var previous = _selected?.Id;
        var accounts = (await _page.Accounts.List(Token)).Where(a => a.Enabled && a.Provider is "imap" or "imap-smtp" or "smtp" or "pop3").ToArray();
        if (_disposed) return;
        _accounts.ItemsSource = accounts; _accounts.SelectedItem = accounts.FirstOrDefault(a => a.Id == previous) ?? accounts.FirstOrDefault();
        await SelectAccount();
    }
    private async Task SelectAccount()
    {
        _syncView?.Dispose(); _syncView = null; _syncTab.Content = null; _syncTab.IsEnabled = false;
        _outboxList.ItemsSource = null; _outboxOffset = 0; _outboxMore = false;
        _selected = _accounts.SelectedItem as MailAccount; _messages.ItemsSource = null; _folders.ItemsSource = null; _offset = 0; _hasMore = false;
        if (_selected is null) { _status.Text = "请先在账户管理创建并启用在线账户。"; _incoming.Panel.IsVisible = _outgoing.Panel.IsVisible = _imap.IsVisible = false; return; }
        var id = _selected.Id; var settings = await _page.Service.Settings(id, Token); if (_disposed) return;
        _presets = await _page.Service.Presets(Token);
        if (_disposed || _selected?.Id != id) return;
        _presetPanel.IsVisible = _selected.Provider == "imap-smtp";
        _presetId = settings.PresetId;
        var choices = new List<Choice> { new("","自定义服务器") };
        choices.AddRange(_presets.Select(p=>new Choice(p.Id,p.Name)));
        if (_presetId is not null && !_presets.Any(p=>p.Id==_presetId))
            choices.Add(new(_presetId,"已停用的预设"));
        _presetChoice.ItemsSource = choices;
        _presetChoice.SelectedItem = choices.First(c=>c.Id==(_presetId??""));
        _presetGuide.Text = _presetId is null ? "" : _presets.FirstOrDefault(p=>p.Id==_presetId)?.Guidance ??
            "原预设插件已停用，账户和邮件仍保留。重新启用插件，或明确选择自定义服务器。";
        _incoming.Load(settings.Incoming, _selected.Provider == "pop3" ? 995 : 993, _selected.Address);
        _outgoing.Load(settings.Outgoing, 465, _selected.Address);
        _incoming.Panel.IsVisible = _selected.Capabilities.HasFlag(AccountCapabilities.Receive);
        _outgoing.Panel.IsVisible = _selected.Capabilities.HasFlag(AccountCapabilities.Send);
        _imap.IsVisible = _selected.Capabilities.HasFlag(AccountCapabilities.Folders);
        if (_imap.IsVisible && _page.ImapSync is { } sync)
        {
            var paths = await sync.CachedFolders(id, Token);
            if (_disposed) return;
            _syncView = new(sync, id, _page.OfflineMarks,_respond); _syncTab.Content = _syncView; _syncTab.IsEnabled = true;
            _syncView.SetFolders(paths);
        }
        await LoadOutbox(0);
        _status.Text = "已载入连接配置。密码留空保留已有凭据；选择操作后才会联网。";
    }
    private async Task ApplyPreset()
    {
        if (_presetChoice.SelectedItem is not Choice choice) return;
        if (choice.Id.Length == 0)
        {
            _presetId = null;_presetGuide.Text = "";
            _status.Text = "已选择自定义服务器，请核对配置后保存。";return;
        }
        var active = await _page.Service.Presets(Token);
        var preset = active.FirstOrDefault(p=>p.Id==choice.Id) ?? throw new InvalidDataException("所选预设插件已停用。");
        var settings = preset.ForAddress(Selected().Address);
        _presetId = preset.Id;_presetGuide.Text = preset.Guidance;
        _incoming.Load(settings.Incoming,993,Selected().Address);_outgoing.Load(settings.Outgoing,465,Selected().Address);
        _status.Text = "已填入 "+preset.Name+" 的加密连接参数，请核对授权码并保存。";
    }

    private MailAccount Selected() => _selected ?? throw new InvalidDataException("请先选择在线账户。");
    private async Task Save()
    {
        var account = Selected();
        await _page.Service.Save(account.Id, new ProtocolSettings(_incoming.Panel.IsVisible ? _incoming.Value() : null, _outgoing.Panel.IsVisible ? _outgoing.Value() : null) { PresetId = _presetId }, _incoming.Secret.Text, _outgoing.Secret.Text, Token);
        await SelectAccount(); if (!_disposed) _status.Text = "连接配置已保存，凭据已加密。尚未连接服务器。";
    }
    private async Task ConnectImap()
    {
        var folders = await _page.Service.Folders(Selected().Id, Token); if (_disposed) return;
        _folders.ItemsSource = folders.Where(f => f.Selectable).ToArray(); _folders.SelectedItem = folders.FirstOrDefault(f => f.Selectable && f.Role == "inbox") ?? folders.FirstOrDefault(f => f.Selectable);
        _syncView?.SetFolders(folders.Where(f => f.Selectable).Select(f => f.Path));
        _status.Text = $"连接成功，发现 {folders.Count} 个目录。特殊用途由服务器属性映射。";
    }
    private async Task ReadSummaries(int offset)
    {
        if (_folders.SelectedItem is not RemoteFolder folder) throw new InvalidDataException("请先连接并选择目录。");
        var result = await _page.Service.Summaries(Selected().Id, folder.Path, offset, 20, Token); if (_disposed) return;
        _messages.ItemsSource = result.Items; _offset = result.Offset; _hasMore = result.HasMore;
        _status.Text = result.Items.Count == 0 ? "此页没有邮件。" : $"已读取 {result.Items.Count} 封摘要 · 第 {offset / 20 + 1} 页。未改变服务器已读状态。";
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _syncView?.Dispose(); _lifetime.Cancel(); _lifetime.Dispose();
        _incoming.Secret.Text = _outgoing.Secret.Text = ""; _accounts.ItemsSource = null; _messages.ItemsSource = null;
    }
    private sealed class EndpointEditor
    {
        public StackPanel Panel { get; } = new() { Spacing = 6 };
        private readonly TextBox _host = new(), _port = new(), _user = new(), _proxyHost = new(), _proxyPort = new() { Text = "1080" }, _pin = new();
        public TextBox Secret { get; } = new() { PasswordChar = '●' };
        private readonly ComboBox _tls = new() { ItemsSource = new[] { "TLS（连接时加密）", "STARTTLS（强制升级加密）" }, SelectedIndex = 0 };
        private readonly ComboBox _auth = new() { ItemsSource = new[] { "密码／授权码", "OAuth2 访问令牌" }, SelectedIndex = 0 };
        private readonly ComboBox _proxy = new() { ItemsSource = new[] { "不使用代理", "HTTP CONNECT", "SOCKS5" }, SelectedIndex = 0 };
        public EndpointEditor(string prefix)
        {
            Panel.Children.Add(new TextBlock { Text = prefix == "incoming" ? "收信服务器" : "发信服务器", FontSize = 20, FontWeight = FontWeight.SemiBold });
            Add(Panel, "服务器", _host, prefix + "-host"); Add(Panel, "端口", _port, prefix + "-port"); Add(Panel, "连接安全", _tls, prefix + "-tls");
            Add(Panel, "登录用户名", _user, prefix + "-user"); Add(Panel, "认证方式", _auth, prefix + "-auth"); Add(Panel, "密码／授权码／访问令牌（留空保留）", Secret, prefix + "-secret");
            var advanced = new StackPanel { Spacing = 6 };
            Add(advanced, "代理", _proxy, prefix + "-proxy"); Add(advanced, "代理服务器", _proxyHost, prefix + "-proxy-host"); Add(advanced, "代理端口", _proxyPort, prefix + "-proxy-port");
            Add(advanced, "信任此服务器证书 SHA-256（可选；仍检查名称和有效期）", _pin, prefix + "-pin");
            advanced.Children.Add(new TextBlock { Text = "访问令牌过期后需更新。证书指纹仅在已独立核实服务器证书时填写。", TextWrapping = TextWrapping.Wrap });
            var expander = new Expander { Header = "代理与证书", Content = advanced }; AutomationProperties.SetAutomationId(expander, prefix + "-advanced"); Panel.Children.Add(expander);
        }
        public void Load(MailEndpoint? endpoint, int port, string user)
        {
            _host.Text = endpoint?.Host ?? ""; _port.Text = (endpoint?.Port ?? port).ToString(); _user.Text = endpoint?.User ?? user; _tls.SelectedIndex = (int)(endpoint?.Tls ?? MailTls.Implicit);
            _auth.SelectedIndex = (int)(endpoint?.Authentication ?? MailAuthentication.Password); _proxy.SelectedIndex = (int)(endpoint?.Proxy ?? MailProxy.None); _proxyHost.Text = endpoint?.ProxyHost ?? "";
            _proxyPort.Text = (endpoint?.ProxyPort ?? 1080).ToString(); _pin.Text = endpoint?.CertificateSha256 ?? ""; Secret.Text = "";
        }
        public MailEndpoint Value()
        {
            if (!int.TryParse(_port.Text, out var port) || !int.TryParse(_proxyPort.Text, out var proxyPort)) throw new InvalidDataException("端口请输入整数。");
            return new((_host.Text ?? "").Trim(), port, (_user.Text ?? "").Trim(), (MailTls)_tls.SelectedIndex, (MailAuthentication)_auth.SelectedIndex, (MailProxy)_proxy.SelectedIndex, (_proxyHost.Text ?? "").Trim(), proxyPort, (_pin.Text ?? "").Replace(" ", "").Trim());
        }
    }
}
