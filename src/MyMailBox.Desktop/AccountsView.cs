using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class AccountsView : UserControl, IDisposable
{
    private readonly IAccountService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _accounts = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _provider = new() { ItemsSource = new[] { "本地邮箱", "IMAP + SMTP", "IMAP（仅收）", "SMTP（仅发）", "POP3（仅收）", "Outlook.com（个人账户）", "公司 Exchange（ActiveSync）" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static readonly string[] BuiltinTypes = ["local", "imap-smtp", "imap", "smtp", "pop3", "outlook-com", "exchange-eas"];
    private string[] Providers = BuiltinTypes;
    private readonly string[] _builtinLabels;
    private readonly TextBox _name = new(), _address = new(), _alias = new(), _tags = new(), _color = new() { Text = "#2563EB" }, _notes = new() { AcceptsReturn = true, MinHeight = 64 }, _order = new() { Text = "0" }, _credential = new() { PasswordChar = '●' };
    private readonly CheckBox _enabled = new() { Content = "启用此账户", IsChecked = true }, _receive = new() { Content = "接收", IsChecked = true }, _send = new() { Content = "发送", IsChecked = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap }, _capability = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _confirm = new() { Content = "我已了解：移除账户和凭据，保留缓存邮件与独立归档。", IsVisible = false };
    private readonly Button _remove;
    private Guid? _id;
    private bool _disposed;

    public AccountsView(IAccountService service)
    {
        _service = service;
        _builtinLabels = _provider.ItemsSource!.Cast<string>().ToArray();
        _accounts.ItemTemplate = new FuncDataTemplate<MailAccount>((a, _) => new TextBlock { Text = a is null ? "" : $"{a.Name} · {(a.Provider == "local" ? "本地" : a.Address)}{(a.Enabled ? "" : " · 已停用")}" });
        var panel = new StackPanel { Spacing = 12, MaxWidth = 760 };
        panel.Children.Add(new TextBlock { Text = "账户管理", FontSize = 28, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "名称可以随时修改，邮件和自动化引用保持不变。普通在线账户到“邮箱连接与收发”配置服务器；个人微软账户到“Outlook.com 邮件”授权。", TextWrapping = TextWrapping.Wrap });
        Add(panel, "已有账户", _accounts, "account-list");
        panel.Children.Add(Action("添加账户", "account-new", () => { Edit(null); return Task.CompletedTask; }));
        Add(panel, "显示名称", _name, "account-name"); Add(panel, "类型", _provider, "account-provider"); Add(panel, "邮箱地址", _address, "account-address");
        Add(panel, "alias（可选，唯一，用于自动化）", _alias, "account-alias"); Add(panel, "标签（逗号分隔）", _tags, "account-tags");
        var row = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 }; var left = new StackPanel { Spacing = 6 }; var right = new StackPanel { Spacing = 6 };
        Add(left, "颜色（#RRGGBB）", _color, "account-color"); Add(right, "排序（数值小的在前）", _order, "account-order"); row.Children.Add(left); Grid.SetColumn(right, 1); row.Children.Add(right); panel.Children.Add(row);
        Add(panel, "备注", _notes, "account-notes");
        var capabilities = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 }; capabilities.Children.Add(_enabled); capabilities.Children.Add(_receive); capabilities.Children.Add(_send); panel.Children.Add(capabilities); panel.Children.Add(_capability);
        AutomationProperties.SetAutomationId(_enabled, "account-enabled"); AutomationProperties.SetAutomationId(_receive, "account-receive"); AutomationProperties.SetAutomationId(_send, "account-send");
        panel.Children.Add(new TextBlock { Text = "收信、发信凭据在“邮箱连接与收发”分别设置，并由 Windows 当前用户加密保护。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Action("保存账户", "account-save", Save));
        _remove = Action("移除账户…", "account-remove", Remove); _remove.IsEnabled = false;
        panel.Children.Add(_remove); panel.Children.Add(_confirm); panel.Children.Add(_status);
        AutomationProperties.SetAutomationId(_confirm, "account-remove-confirm"); AutomationProperties.SetAutomationId(_status, "account-status");
        Content = new ScrollViewer { Content = panel };
        _accounts.SelectionChanged += (_, _) => { if (_accounts.SelectedItem is MailAccount account) Edit(account); };
        _provider.SelectionChanged += (_, _) => UpdateCapabilities();
        AttachedToVisualTree += async (_, _) => await Guard(() => Reload(null));
        UpdateCapabilities();
    }

    private static void Add(StackPanel panel, string label, Control input, string id)
    {
        AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label);
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Medium }); panel.Children.Add(input);
    }
    private Button Action(string label, string id, Func<Task> action)
    {
        var button = new Button { Content = label }; AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await Guard(action); } finally { if (!_disposed) button.IsEnabled = id != "account-remove" || _id is not null; } }; return button;
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { if (!_disposed) _status.Text = "未完成：" + error.Message; }
    }
    private async Task Reload(Guid? selected)
    {
        var accounts = await _service.List(_lifetime.Token); if (_disposed) return;
        var external = _service.ProviderTypes;
        var missing = accounts.Select(a => a.Provider).Where(p => p.Contains('/') && !external.Any(e => e.Id == p)).Distinct().ToArray();
        Providers = [.. BuiltinTypes, .. external.Select(e => e.Id), .. missing];
        _provider.ItemsSource = _builtinLabels.Concat(external.Select(e => e.Name)).Concat(missing.Select(id => id + "（插件已停用）")).ToArray();
        _provider.SelectedIndex = 0;
        _accounts.ItemsSource = accounts; _accounts.SelectedItem = accounts.FirstOrDefault(a => a.Id == selected);
    }
    private void Edit(MailAccount? account)
    {
        _id = account?.Id; _provider.IsEnabled = account is null;
        _name.Text = account?.Name ?? ""; _address.Text = account?.Address ?? ""; _alias.Text = account?.Alias ?? "";
        _tags.Text = string.Join(", ", account?.Tags ?? []); _color.Text = account?.Color ?? "#2563EB"; _notes.Text = account?.Notes ?? ""; _order.Text = (account?.Order ?? 0).ToString();
        _provider.SelectedIndex = account is null ? 0 : Array.IndexOf(Providers, account.Provider);
        _enabled.IsChecked = account?.Enabled ?? true; _receive.IsChecked = account?.Capabilities.HasFlag(AccountCapabilities.Receive) ?? true; _send.IsChecked = account?.Capabilities.HasFlag(AccountCapabilities.Send) ?? true;
        _credential.Text = ""; _confirm.IsVisible = false; _confirm.IsChecked = false; _remove.Content = "移除账户…"; _remove.IsEnabled = account is not null;
        _status.Text = account?.AuthenticationState ?? "填写账户信息后保存。"; UpdateCapabilities();
    }
    private void UpdateCapabilities()
    {
        var provider = Providers[Math.Max(0, _provider.SelectedIndex)]; var local = provider == "local";
        _address.IsEnabled = !local;
        _credential.IsEnabled = !local && provider != "outlook-com";
        _receive.IsEnabled = _send.IsEnabled = provider == "imap-smtp";
        if (provider != "imap-smtp") { _receive.IsChecked = provider is "imap" or "pop3" or "outlook-com"; _send.IsChecked = provider == "smtp" || provider == "outlook-com" && (_accounts.SelectedItem as MailAccount)?.Capabilities.HasFlag(AccountCapabilities.Send) == true; }
        if(provider=="exchange-eas")
        {
            _credential.IsEnabled=false; _receive.IsChecked=true;
            _send.IsChecked=(_accounts.SelectedItem as MailAccount)?.Capabilities.HasFlag(AccountCapabilities.Send)==true;
            _capability.Text="保存后到“Exchange 连接”填写服务器、登录身份和设备授权。";return;
        }
        if (provider.Contains('/'))
        {
            var type = _service.ProviderTypes.FirstOrDefault(p => p.Id == provider);
            _receive.IsChecked = type?.Features.HasFlag(MailboxFeatures.Read) ?? false;
            _send.IsChecked = type?.Features.HasFlag(MailboxFeatures.Send) ?? false;
            _credential.IsEnabled = false;
            _capability.Text = type == null ? $"所需邮箱插件 {provider} 已停用，可重新启用后连接。" : "请在“邮箱浏览与整理”中连接这个账户。";
            return;
        }
        _capability.Text = local ? "本地账户用于保存和整理邮件，不支持联网收发。" : provider == "outlook-com" ? "在“Outlook.com 邮件”登录个人账户。发送与整理需另外授权。" : provider == "smtp" ? "SMTP 仅发送，不支持收取邮件。" : provider is "imap" or "pop3" ? "此类型仅接收；发送需要单独的 SMTP 账户。" : "可单独关闭收取或发送；在“邮箱连接与收发”配置各自的服务器和凭据。";
    }
    private async Task Save()
    {
        if (!int.TryParse(_order.Text, out var order)) throw new InvalidDataException("排序请输入整数。");
        var capabilities = (_receive.IsChecked == true ? AccountCapabilities.Receive : 0) | (_send.IsChecked == true ? AccountCapabilities.Send : 0);
        var saved = await _service.Save(new(_id, _name.Text ?? "", Providers[Math.Max(0, _provider.SelectedIndex)], _address.Text ?? "", _alias.Text,
            (_tags.Text ?? "").Split([',', '，']), _color.Text ?? "", _notes.Text ?? "", order, _enabled.IsChecked == true, capabilities), _credential.Text, _lifetime.Token);
        _credential.Text = ""; await Reload(saved.Id); if (!_disposed) _status.Text = "账户已保存。" + saved.AuthenticationState;
    }
    private async Task Remove()
    {
        if (_id is not { } id) return;
        if (!_confirm.IsVisible) { _confirm.IsVisible = true; _remove.Content = "确认移除账户"; _status.Text = "账户不再参与收发，凭据将移除；已有缓存邮件与独立归档保留在工作空间。"; return; }
        if (_confirm.IsChecked != true) { _status.Text = "请先阅读并勾选移除范围。"; return; }
        await _service.Remove(id, _lifetime.Token); await Reload(null); Edit(null); _status.Text = "账户已移除，原有邮件和独立归档均保留。";
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); _credential.Text = ""; _accounts.ItemsSource = null; }
}
