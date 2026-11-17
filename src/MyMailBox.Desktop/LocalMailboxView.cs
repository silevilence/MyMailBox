using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class LocalMailboxView : UserControl, IDisposable
{
    private readonly ILocalMailboxService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _mailboxes = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _parent = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, _target = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new(), _alias = new(), _tags = new(), _folderName = new(), _file = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _panel = new() { Spacing = 10 };
    private bool _disposed, _busy;
    private int _revision;
    public LocalMailboxView(ILocalMailboxService service)
    {
        _service = service;
        _panel.Children.Add(new TextBlock { Text = "本地邮箱管理", FontSize = 28, FontWeight = FontWeight.SemiBold });
        _panel.Children.Add(new TextBlock { Text = "关闭或在账户管理中移除只隐藏邮箱，邮件原文和附件仍保留。这里可以重新打开保留的邮箱。名称、标签、颜色和备注可在账户管理中修改。", TextWrapping = TextWrapping.Wrap });
        _mailboxes.ItemTemplate = new FuncDataTemplate<LocalMailboxInfo>((m, _) => new TextBlock { Text = m is null ? "" : $"{m.Name} · {(m.Removed ? "已移除，归档保留" : m.Open ? "已打开" : "已关闭")} · {m.Messages} 封 · {string.Join(" / ", m.Tags)}" });
        Add("已有本地邮箱", _mailboxes, "local-mailboxes");
        Add("名称（新建时使用）", _name, "local-name"); Add("alias（可选；重新打开时使用）", _alias, "local-alias"); Add("标签（新建时使用，逗号分隔）", _tags, "local-tags");
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Action("新建本地邮箱", "local-create", async () =>
        {
            var created = await _service.Create(new(null, _name.Text ?? "", "local", Alias: _alias.Text, Tags: (_tags.Text ?? "").Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)), _lifetime.Token);
            await Refresh(created.Id); _status.Text = "本地邮箱已创建，可新建目录或导入完整原文。";
        }));
        actions.Children.Add(Action("打开 / 重新打开", "local-open", () => SetOpen(true)));
        actions.Children.Add(Action("关闭并保留归档", "local-close", () => SetOpen(false)));
        actions.Children.Add(Action("刷新列表", "local-refresh", () => Refresh())); _panel.Children.Add(actions);
        _parent.ItemTemplate = new FuncDataTemplate<MailFolder>((f, _) => new TextBlock { Text = f?.Path });
        _target.ItemTemplate = new FuncDataTemplate<MailFolder>((f, _) => new TextBlock { Text = f?.Path });
        Add("父目录（根目录表示新建顶层目录）", _parent, "local-parent"); Add("新目录名称", _folderName, "local-folder-name");
        _panel.Children.Add(Action("创建归档目录", "local-folder-create", async () =>
        {
            var mailbox = Selected(); var parent = (_parent.SelectedItem as MailFolder)?.Id;
            var folder = await _service.CreateFolder(mailbox.Id, parent == Guid.Empty ? null : parent, _folderName.Text ?? "", _lifetime.Token);
            await LoadFolders(); _target.SelectedItem = (_target.ItemsSource as IEnumerable<MailFolder>)?.FirstOrDefault(f => f.Id == folder.Id); _status.Text = "归档目录已创建。";
        }));
        Add("接收完整原文的目录", _target, "local-target"); Add("EML 文件路径", _file, "local-eml");
        var import = new WrapPanel { Orientation = Orientation.Horizontal };
        import.Children.Add(Action("选择 EML…", "local-browse", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "选择完整 EML 原文" }); if (files.Count > 0) _file.Text = files[0].Path.LocalPath;
        }));
        import.Children.Add(Action("导入到选定目录", "local-import", async () =>
        {
            if (_target.SelectedItem is not MailFolder folder) throw new InvalidDataException("请选择接收目录。");
            var file = new FileInfo(_file.Text ?? ""); if (!file.Exists || file.Length > 25 * 1024 * 1024) throw new InvalidDataException("请选择不超过 25 MiB 的 EML 文件。");
            await _service.Archive(folder.Id, await File.ReadAllBytesAsync(file.FullName, _lifetime.Token), _lifetime.Token);
            await Refresh(folder.AccountId); _status.Text = "完整 MIME 与附件已归档。可切换“本地邮箱”页面离线阅读，源文件保持不变。";
        })); _panel.Children.Add(import);
        _panel.Children.Add(new TextBlock { Text = service.StorageDescription, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(_status, "local-status");
        var layout = new DockPanel(); DockPanel.SetDock(_status, Dock.Bottom); layout.Children.Add(_status); layout.Children.Add(new ScrollViewer { Content = _panel }); Content = layout;
        _mailboxes.SelectionChanged += async (_, _) =>
        {
            if (_mailboxes.SelectedItem is LocalMailboxInfo mailbox) { _alias.Text = mailbox.Alias; _name.Text = mailbox.Name; _tags.Text = string.Join(", ", mailbox.Tags); }
            await Guard(LoadFolders, false);
        };
        AttachedToVisualTree += async (_, _) => await Guard(() => Refresh());
    }
    private LocalMailboxInfo Selected() => _mailboxes.SelectedItem as LocalMailboxInfo ?? throw new InvalidDataException("请选择本地邮箱。");
    private void Add(string label, Control input, string id) { _panel.Children.Add(new TextBlock { Text = label }); _panel.Children.Add(input); AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label); }
    private Button Action(string label, string id, Func<Task> action)
    { var button = new Button { Content = label }; AutomationProperties.SetAutomationId(button, id); button.Click += async (_, _) => await Guard(action); return button; }
    private async Task Guard(Func<Task> action, bool lockForm = true)
    {
        if (lockForm && _busy) return;
        if (lockForm) { _busy = true; _panel.IsEnabled = false; }
        try { await action(); } catch (OperationCanceledException) { } catch (Exception e) { if (!_disposed) _status.Text = "操作未完成：" + e.Message; }
        finally { if (lockForm) { _busy = false; _panel.IsEnabled = true; } }
    }
    private async Task Refresh(Guid? id = null)
    {
        id ??= (_mailboxes.SelectedItem as LocalMailboxInfo)?.Id;
        var items = await _service.List(_lifetime.Token); if (_disposed) return;
        _mailboxes.ItemsSource = items; _mailboxes.SelectedItem = items.FirstOrDefault(m => m.Id == id) ?? items.FirstOrDefault();
    }
    private async Task LoadFolders()
    {
        var revision = ++_revision;
        if (_mailboxes.SelectedItem is not LocalMailboxInfo mailbox) { _parent.ItemsSource = null; _target.ItemsSource = null; return; }
        var folders = await _service.Folders(mailbox.Id, _lifetime.Token); if (_disposed || revision != _revision) return;
        _parent.ItemsSource = new[] { new MailFolder(Guid.Empty, mailbox.Id, "根目录", "根目录") }.Concat(folders).ToArray(); _parent.SelectedIndex = 0;
        _target.ItemsSource = folders; _target.SelectedIndex = folders.Count > 0 ? 0 : -1;
    }
    private async Task SetOpen(bool open)
    {
        var mailbox = Selected(); await _service.SetOpen(mailbox.Id, open, _alias.Text, _lifetime.Token); await Refresh(mailbox.Id);
        _status.Text = open ? "本地邮箱已打开，稳定 ID 与原文保留。请重新进入阅读页面刷新目录。" : "已关闭，底层归档保留；阅读和导出入口不再访问该邮箱。";
    }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
