using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;
using System.Text;

namespace MyMailBox.Desktop;

/// <summary>Host-owned controls; plugin services return only SDK records.</summary>
public sealed class MailboxView : UserControl, IDisposable
{
    private readonly MailReader _reader;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _reading;
    private readonly ComboBox _folders = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _messages = new() { SelectionMode = Avalonia.Controls.SelectionMode.Multiple };
    private readonly TextBox _file = new() { PlaceholderText = "EML 文件路径", MinWidth = 200 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _body = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _import;
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "纯文本", "MIME 结构与附件", "原始邮件" }, SelectedIndex = 0 };
    private ParsedMail? _parsed;
    private byte[]? _raw;
    private int _offset;
    private int _listVersion;
    private bool _disposed;
    private CancellationTokenSource? _exporting;
    private readonly TextBox _exportDirectory = new();
    private readonly ComboBox _exportFormat = new();
    private readonly IReadOnlyList<IMessageExporter> _exporters;

    public MailboxView(MailReader reader, IReadOnlyList<IMessageExporter> exporters)
    {
        _reader = reader;
        _exporters = exporters; _exportFormat.ItemsSource = exporters.Select(e => e.Name).ToArray(); _exportFormat.SelectedIndex = exporters.Count > 0 ? 0 : -1;
        AutomationProperties.SetAutomationId(_file, "mail-file"); AutomationProperties.SetName(_file, "EML 文件路径");
        AutomationProperties.SetAutomationId(_folders, "mail-folders"); AutomationProperties.SetName(_folders, "邮件目录");
        AutomationProperties.SetAutomationId(_messages, "mail-list"); AutomationProperties.SetName(_messages, "邮件列表");
        AutomationProperties.SetAutomationId(_body, "mail-body"); AutomationProperties.SetName(_body, "邮件正文");
        AutomationProperties.SetAutomationId(_status, "mail-status");
        _messages.ItemTemplate = new FuncDataTemplate<MailSummary>((item, _) => new TextBlock
        {
            Text = item is null ? "" : $"{item.Subject}\n{item.From} · {item.Date:yyyy-MM-dd HH:mm}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6), MaxWidth = 300
        });
        _folders.ItemTemplate = new FuncDataTemplate<MailFolder>((item, _) => new TextBlock { Text = item?.Path });
        _previous = Button("上一页", "mail-previous", () => LoadPage(Math.Max(0, _offset - 50)));
        _next = Button("下一页", "mail-next", () => LoadPage(_offset + 50));
        _import = Button("导入 EML", "mail-import", Import);
        var browse = Button("选择文件…", "mail-browse", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "选择邮件", AllowMultiple = false });
            if (files.Count > 0) _file.Text = files[0].Path.LocalPath;
        });
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(new TextBlock { Text = "本地邮箱", FontSize = 28, FontWeight = FontWeight.SemiBold });
        header.Children.Add(new TextBlock { Text = "原始邮件保存在当前工作空间；导入不会移动或修改源文件。", TextWrapping = TextWrapping.Wrap });
        header.Children.Add(_folders);
        var fileGrid = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        fileGrid.Children.Add(_file); Grid.SetColumn(browse, 1); fileGrid.Children.Add(browse); Grid.SetColumn(_import, 2); fileGrid.Children.Add(_import);
        header.Children.Add(fileGrid);
        AutomationProperties.SetAutomationId(_mode, "mail-mode"); AutomationProperties.SetName(_mode, "阅读模式"); header.Children.Add(_mode);
        _mode.SelectionChanged += (_, _) => ShowContent();
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_previous); buttons.Children.Add(_next); buttons.Children.Add(Button("刷新", "mail-refresh", () => LoadPage(_offset)));
        buttons.Children.Add(Button("取消读取", "mail-cancel", () => { _reading?.Cancel(); return Task.CompletedTask; }));
        footer.Children.Add(buttons); footer.Children.Add(_status);
        var exportPanel = new StackPanel { Spacing = 8 };
        exportPanel.Children.Add(new TextBlock { Text = "Ctrl/Shift 多选当前页邮件；同名文件保留，重复导出复用相同原文。", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(_exportDirectory, "mail-export-directory"); AutomationProperties.SetName(_exportDirectory, "导出目录");
        AutomationProperties.SetAutomationId(_exportFormat, "mail-export-format"); AutomationProperties.SetName(_exportFormat, "导出格式");
        exportPanel.Children.Add(new TextBlock { Text = "导出目录" }); exportPanel.Children.Add(_exportDirectory); exportPanel.Children.Add(_exportFormat);
        var exportActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        exportActions.Children.Add(Button("导出所选邮件", "mail-export", Export));
        exportActions.Children.Add(Button("取消导出", "mail-export-cancel", () => { _exporting?.Cancel(); return Task.CompletedTask; })); exportPanel.Children.Add(exportActions);
        footer.Children.Insert(1, new Expander { Header = "导出邮件", Content = exportPanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        var content = new Grid { ColumnDefinitions = new("2*,3*"), ColumnSpacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        content.Children.Add(_messages); Grid.SetColumn(_body, 1); content.Children.Add(_body);
        var layout = new DockPanel(); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer); layout.Children.Add(content); Content = layout;
        _folders.SelectionChanged += async (_, _) => await Guard(() => LoadPage(0));
        _messages.SelectionChanged += async (_, _) => await Guard(ReadSelected);
        AttachedToVisualTree += async (_, _) => await Guard(Initialize);
    }

    private Button Button(string label, string id, Func<Task> action)
    {
        var button = new Button { Content = label }; AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await Guard(action); } finally { if (!_disposed && id is not "mail-previous" and not "mail-next") button.IsEnabled = true; } };
        return button;
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "读取已取消，邮件仍保留。"; }
        catch (Exception error) { if (!_disposed) _status.Text = "操作未完成：" + error.Message; }
    }

    private async Task Initialize()
    {
        var folders = await _reader.Store.Folders(_lifetime.Token);
        if (_disposed) return;
        _folders.ItemsSource = folders; _folders.SelectedIndex = folders.Count > 0 ? 0 : -1;
    }

    private async Task LoadPage(int offset)
    {
        if (_folders.SelectedItem is not MailFolder folder) return;
        var version = ++_listVersion;
        _reading?.Cancel(); _body.Text = ""; _parsed = null; _raw = null; _status.Text = "正在加载邮件列表…";
        var page = await _reader.Store.List(folder.Id, offset, 50, _lifetime.Token);
        if (_disposed || version != _listVersion) return;
        _offset = offset; _messages.ItemsSource = page.Items; _previous.IsEnabled = offset > 0; _next.IsEnabled = page.HasMore;
        _status.Text = page.Items.Count == 0 ? "此目录还没有邮件。选择 EML 文件开始导入。" : $"第 {offset + 1}–{offset + page.Items.Count} 封 · 选择邮件阅读正文";
    }

    private async Task Import()
    {
        if (_folders.SelectedItem is not MailFolder folder) return;
        var path = _file.Text?.Trim() ?? "";
        if (!File.Exists(path)) throw new FileNotFoundException("请先选择存在的 EML 文件。");
        _status.Text = "正在导入，源文件保持不变…";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > Mail.MimeReader.MaxMessageBytes) throw new InvalidDataException("文件超过 25 MiB 导入限制。");
        var raw = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(raw, _lifetime.Token);
        var metadata = await _reader.Parser.Parse(raw, _lifetime.Token);
        var id = await _reader.Store.Import(folder.Id, raw, metadata, _lifetime.Token);
        if (_disposed) return;
        await LoadPage(0); _status.Text = "导入完成，原文和摘要已保存。";
        _messages.SelectedItem = (_messages.ItemsSource as IReadOnlyList<MailSummary>)?.FirstOrDefault(m => m.Id == id);
    }

    private async Task ReadSelected()
    {
        _reading?.Cancel(); _reading?.Dispose();
        if (_messages.SelectedItem is not MailSummary summary) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _reading = cancellation;
        try
        {
            _body.Text = "正在读取…";
            var raw = await _reader.Store.Read(summary.Id, cancellation.Token);
            var mail = await _reader.Parser.Parse(raw, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            _parsed = mail; _raw = raw; ShowContent();
        }
        finally { if (ReferenceEquals(_reading, cancellation)) _reading = null; }
    }

    private async Task Export()
    {
        if (_exportFormat.SelectedIndex < 0) throw new InvalidOperationException("没有可用导出插件。");
        var selected = _messages.SelectedItems?.OfType<MailSummary>().Select(m => new ExportItem(m.Id, m.Subject)).ToArray() ?? [];
        if (selected.Length == 0) throw new InvalidDataException("请先选择要导出的邮件。");
        if (string.IsNullOrWhiteSpace(_exportDirectory.Text)) throw new InvalidDataException("请填写导出目录。");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _exporting = cancellation;
        try
        {
            var report = await _exporters[_exportFormat.SelectedIndex].Export(new(selected, _exportDirectory.Text), new Progress<ExportItemResult>(r => { if (!_disposed) _status.Text = "导出中：" + r.Detail; }), cancellation.Token);
            if (!_disposed) _status.Text = string.Join("\n", report.Items.Select(r => r.Detail + (r.Path is null ? "" : " " + r.Path)));
        }
        finally { _exporting = null; }
    }

    private void ShowContent()
    {
        if (_parsed is not { } mail || _raw is null) return;
        var warnings = mail.Warnings?.Count > 0 ? "阅读提示：" + string.Join("；", mail.Warnings) + "\n\n" : "";
        _body.Text = _mode.SelectedIndex switch
        {
            1 => warnings + string.Join("\n", (mail.Parts ?? []).Select(p => new string(' ', p.Depth * 2) + $"{p.Path} · {p.ContentType} · {p.Name}{(p.Attachment ? " · 附件" : "")}{(p.ContentId is null ? "" : " · CID:" + p.ContentId)}")),
            2 => (_raw.Length > 128 * 1024 ? "原文预览显示前 128 KiB；完整原始字节保存在工作空间。\n\n" : "") + Encoding.UTF8.GetString(_raw.AsSpan(0, Math.Min(_raw.Length, 128 * 1024))),
            _ => warnings + $"{mail.Subject}\n发件人：{mail.From}\n收件人：{mail.To}\n抄送：{mail.Cc}\n日期：{mail.Date:yyyy-MM-dd HH:mm zzz}\n\n{mail.Text}"
        };
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _reading?.Cancel(); _exporting?.Cancel(); _lifetime.Cancel(); _lifetime.Dispose(); _messages.ItemsSource = null; _folders.ItemsSource = null; _raw = null; _parsed = null; }
}
