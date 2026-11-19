using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class OutlookAccountsView : UserControl, IDisposable
{
    private readonly IOutlookAccountImport _service;
    private readonly CancellationTokenSource _life = new();
    private readonly StackPanel _form = new() { Spacing = 8, MaxWidth = 800 };
    private readonly ComboBox _profiles = new(), _accounts = new(), _matches = new();
    private readonly TextBox _name = new(), _email = new(), _tags = new();
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap }, _result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _fields = new() { Spacing = 8, IsVisible = false };
    private readonly EndpointFields _incoming = new("收件"), _outgoing = new("发件");
    private readonly Button _commit;
    private bool _busy;

    public OutlookAccountsView(IOutlookAccountImport service)
    {
        _service = service;
        _form.Children.Add(new TextBlock { Text = "导入经典 Outlook 账户", FontSize = 26 });
        _form.Children.Add(new TextBlock { Text = "选择本机 Profile 预览可读取的 Internet 邮箱设置。确认后，在“协议收发”中重新授权。", TextWrapping = TextWrapping.Wrap });
        Button(_form, "查找经典 Outlook Profile", "outlook-discover", async () =>
        {
            var profiles = await service.Profiles(_life.Token);
            _profiles.ItemsSource = profiles;
            _profiles.SelectedIndex = profiles.Count > 0 ? 0 : -1;
            _result.Text = profiles.Count == 0 ? "未发现经典 Outlook 16 Profile。新 Outlook 可在“账户与身份”中重新添加。" : "请选择要读取的 Profile。";
        });
        Field(_form, "来源 Profile", _profiles, "outlook-profile");
        _profiles.SelectionChanged += (_, _) => { _accounts.ItemsSource = null; _fields.IsVisible = false; };
        Button(_form, "读取此 Profile 的账户", "outlook-preview", async () =>
        {
            if (_profiles.SelectedItem is not string profile) throw new InvalidDataException("请先选择来源 Profile。");
            var entries = await service.Preview(profile, _life.Token);
            _accounts.ItemsSource = entries;
            _accounts.SelectedIndex = entries.Count > 0 ? 0 : -1;
            _result.Text = entries.Count == 0 ? "这个 Profile 没有可读取的 Internet 账户配置。" : $"发现 {entries.Count} 个账户，可先核对来源与缺失字段。";
        });
        _accounts.ItemTemplate = new FuncDataTemplate<OutlookAccountPreview>((a, _) => new TextBlock { Text = a == null ? "" : $"{a.Name}  {a.Address}" });
        Field(_form, "来源账户", _accounts, "outlook-source");
        _form.Children.Add(_source);
        _form.Children.Add(_fields);
        Field(_fields, "显示名", _name, "outlook-name");
        Field(_fields, "邮箱地址", _email, "outlook-address");
        Field(_fields, "本地标签（逗号分隔）", _tags, "outlook-tags");
        _fields.Children.Add(_incoming.Panel);
        _fields.Children.Add(_outgoing.Panel);
        _matches.ItemTemplate = new FuncDataTemplate<MailAccount>((a, _) => new TextBlock { Text = a == null ? "" : $"{a.Name} · {a.Address} · {a.Alias}" });
        Button(_fields, "查找同类型、同地址的已有账户", "outlook-find-match", async () =>
        {
            var source = Selection();
            var matches = await service.Matches(source.Provider, _email.Text ?? "", _life.Token);
            _matches.ItemsSource = matches;
            _matches.SelectedIndex = -1;
            _result.Text = matches.Count == 0 ? "没有匹配项，可以创建新账户。" : "请选择要沿用的账户。映射只引用已有账户及其连接设置。";
        });
        Field(_fields, "映射到已有账户（可选）", _matches, "outlook-match");
        _commit = Button(_fields, "确认并创建账户", "outlook-confirm", async () =>
        {
            var source = Selection();
            var matched = (_matches.SelectedItem as MailAccount)?.Id;
            var edit = new AccountEdit(null, _name.Text ?? "", source.Provider, _email.Text ?? "", Tags: (_tags.Text ?? "").Split([',','，'], StringSplitOptions.RemoveEmptyEntries));
            var settings = matched == null ? new ProtocolSettings(_incoming.Read(), _outgoing.Read()) : new();
            var result = await service.Import(edit, settings, matched, _life.Token);
            _result.Text = matched != null ? $"已映射到 {result.Name}，沿用原有连接配置。" : $"已创建 {result.Name}。请到“协议收发”选择此账户并填写凭据。";
            _fields.IsVisible = false;
            _accounts.SelectedIndex = -1;
        });
        _matches.SelectionChanged += (_, _) => _commit.Content = _matches.SelectedItem == null ? "确认并创建账户" : "确认映射到选中账户";
        _accounts.SelectionChanged += (_, _) => ShowSelection();
        _email.TextChanged += (_, _) => _matches.ItemsSource = null;
        Button(_fields, "取消本次导入", "outlook-cancel", () =>
        {
            _accounts.SelectedIndex = -1;
            _result.Text = "已取消本次导入。";
            return Task.CompletedTask;
        });
        AutomationProperties.SetAutomationId(_result, "outlook-status");
        _form.Children.Add(_result);
        Content = new ScrollViewer { Content = _form };
    }

    private OutlookAccountPreview Selection() => _accounts.SelectedItem as OutlookAccountPreview ?? throw new InvalidDataException("请选择来源账户。");

    private void ShowSelection()
    {
        _matches.ItemsSource = null;
        if (_accounts.SelectedItem is not OutlookAccountPreview a) { _fields.IsVisible = false; _source.Text = ""; return; }
        _source.Text = $"来源：{a.Source}。读取到的字段已填入，空字段需补充。TLS 方式须与服务方核对。";
        _fields.IsVisible = a.Provider != "";
        if (!_fields.IsVisible) { _source.Text += " 此提供者未提供可映射的 Internet 协议字段，请在对应提供者页面连接。"; return; }
        _name.Text = a.Name; _email.Text = a.Address; _tags.Text = "";
        _incoming.Fill(a.Incoming, a.Provider != "smtp"); _outgoing.Fill(a.Outgoing, a.Provider is "smtp" or "imap-smtp");
        if (a.Provider == "pop3" && a.Outgoing != null) _source.Text += " POP3 仅导入收件配置，SMTP 发件需单独添加。";
    }

    private Button Button(StackPanel panel, string label, string id, Func<Task> action)
    {
        var button = new Button { Content = label };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; _form.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception e) { _result.Text = e.Message; }
            finally { _busy = false; _form.IsEnabled = true; }
        };
        panel.Children.Add(button); return button;
    }
    private static void Field(StackPanel panel, string label, Control control, string id)
    {
        panel.Children.Add(new TextBlock { Text = label });
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(control, label); AutomationProperties.SetAutomationId(control, id);
        panel.Children.Add(control);
    }
    private sealed class EndpointFields
    {
        public StackPanel Panel { get; } = new() { Spacing = 4 };
        private readonly TextBox _host = new(), _port = new(), _user = new();
        private readonly ComboBox _tls = new() { ItemsSource = new[] { "隐式 TLS", "STARTTLS" } };
        public EndpointFields(string title)
        {
            var prefix = title == "收件" ? "outlook-in-" : "outlook-out-";
            Field(Panel, title + "服务器", _host, prefix + "host"); Field(Panel, title + "端口", _port, prefix + "port");
            Field(Panel, title + "登录名", _user, prefix + "user"); Field(Panel, title + "加密方式", _tls, prefix + "tls");
        }
        public void Fill(OutlookEndpoint? e, bool visible)
        {
            Panel.IsVisible = visible; _host.Text = e?.Host; _port.Text = e?.Port?.ToString(); _user.Text = e?.User; _tls.SelectedIndex = -1;
        }
        public MailEndpoint? Read()
        {
            if (!Panel.IsVisible) return null;
            if (!int.TryParse(_port.Text, out var number) || _tls.SelectedIndex < 0) throw new InvalidDataException("请核对端口并选择加密方式。");
            return new(_host.Text?.Trim() ?? "", number, _user.Text?.Trim() ?? "", _tls.SelectedIndex == 0 ? MailTls.Implicit : MailTls.StartTls);
        }
    }
    public void Dispose() { _life.Cancel(); _life.Dispose(); }
}
