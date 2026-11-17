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
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "纯文本", "MIME 结构与附件", "原始邮件", "安全 HTML" }, SelectedIndex = 0 };
    private readonly ComboBox _encoding = new() { ItemsSource = new[] { "自动编码", "UTF-8", "GB18030 / GBK", "Big5", "日文 Shift-JIS", "Western Windows-1252" }, SelectedIndex = 0 };
    private static readonly string?[] Encodings = [null, "utf-8", "gb18030", "big5", "shift_jis", "windows-1252"];
    private readonly ContentControl _readingContent = new();
    private SecureHtmlView? _htmlView;
    private int _htmlRevision;
    private Uri? _pendingLink;
    private readonly TextBlock _linkPrompt = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _remoteScope = new() { ItemsSource = new[] { "仅本封（记住选择）", "此发件地址（整个工作空间）", "此域名（仅精确域，整个工作空间）" }, SelectedIndex = 0 };
    private readonly TextBlock _remoteExplanation = new() { TextWrapping = TextWrapping.Wrap };
    private Guid? _messageId;
    private CancellationTokenSource? _htmlCancellation;
    private ParsedMail? _parsed;
    private byte[]? _raw;
    private int _offset;
    private int _listVersion;
    private bool _disposed;
    private CancellationTokenSource? _exporting;
    private readonly TextBox _exportDirectory = new();
    private readonly ComboBox _exportFormat = new();
    private readonly IReadOnlyList<IMessageExporter> _exporters;
    private readonly ComboBox _attachments = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _attachmentDirectory = new();
    private readonly Button _attachmentOpen;
    private SavedAttachment? _savedAttachment;
    private CancellationTokenSource? _savingAttachment;

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
        AutomationProperties.SetAutomationId(_mode, "mail-mode"); AutomationProperties.SetName(_mode, "阅读模式");
        AutomationProperties.SetAutomationId(_encoding, "mail-encoding"); AutomationProperties.SetName(_encoding, "正文编码");
        var readingOptions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; readingOptions.Children.Add(_mode); readingOptions.Children.Add(_encoding); header.Children.Add(readingOptions);
        _mode.SelectionChanged += (_, _) => ShowContent();
        _encoding.SelectionChanged += async (_, _) => await Guard(ReadSelected);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_previous); buttons.Children.Add(_next); buttons.Children.Add(Button("刷新", "mail-refresh", () => LoadPage(_offset)));
        buttons.Children.Add(Button("取消读取", "mail-cancel", () => { _reading?.Cancel(); return Task.CompletedTask; }));
        buttons.Children.Add(Button("缩小", "mail-zoom-out", () => { _htmlView?.Zoom(-0.1); return Task.CompletedTask; }));
        buttons.Children.Add(Button("放大", "mail-zoom-in", () => { _htmlView?.Zoom(0.1); return Task.CompletedTask; }));
        footer.Children.Add(buttons); footer.Children.Add(_status);
        footer.Children.Add(_linkPrompt);
        footer.Children.Add(Button("确认在浏览器打开上方链接", "mail-open-link", () =>
        {
            if (_pendingLink is { } link) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); _pendingLink = null; _linkPrompt.Text = ""; }
            return Task.CompletedTask;
        }));
        var exportPanel = new StackPanel { Spacing = 8 };
        exportPanel.Children.Add(new TextBlock { Text = "Ctrl/Shift 多选当前页邮件；同名文件保留，重复 EML 导出复用相同原文。PDF/HTML/纯文本是阅读副本，此入口不另存附件；需要附件选项与结果预览时请使用“批量导出”。", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(_exportDirectory, "mail-export-directory"); AutomationProperties.SetName(_exportDirectory, "导出目录");
        AutomationProperties.SetAutomationId(_exportFormat, "mail-export-format"); AutomationProperties.SetName(_exportFormat, "导出格式");
        exportPanel.Children.Add(new TextBlock { Text = "导出目录" }); exportPanel.Children.Add(_exportDirectory); exportPanel.Children.Add(_exportFormat);
        var exportActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        exportActions.Children.Add(Button("导出所选邮件", "mail-export", Export));
        exportActions.Children.Add(Button("取消导出", "mail-export-cancel", () => { _exporting?.Cancel(); return Task.CompletedTask; })); exportPanel.Children.Add(exportActions);
        footer.Children.Insert(1, new Expander { Header = "导出邮件", Content = exportPanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        var remotePanel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(_remoteScope, "mail-remote-scope"); AutomationProperties.SetName(_remoteScope, "远程内容放行范围");
        AutomationProperties.SetAutomationId(_remoteExplanation, "mail-remote-preview");
        remotePanel.Children.Add(_remoteScope); remotePanel.Children.Add(_remoteExplanation);
        remotePanel.Children.Add(new TextBlock { Text = "地址/域规则根据邮件头匹配，不代表身份已验证。加载可能向资源服务器透露网络地址；脚本和额外子请求仍被阻止。", TextWrapping = TextWrapping.Wrap });
        var remoteActions = new WrapPanel { Orientation = Orientation.Horizontal };
        remoteActions.Children.Add(Button("预览范围", "mail-remote-preview-action", PreviewRemote));
        remoteActions.Children.Add(Button("按所选范围放行并加载", "mail-remote-allow", () => SetRemote(true)));
        remoteActions.Children.Add(Button("撤销所选范围", "mail-remote-revoke", () => SetRemote(false))); remotePanel.Children.Add(remoteActions);
        footer.Children.Insert(1, new Expander { Header = "远程多媒体", Content = remotePanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        var attachmentPanel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(_attachments, "mail-attachments"); AutomationProperties.SetName(_attachments, "附件和内联内容");
        _attachments.ItemTemplate = new FuncDataTemplate<MimePartInfo>((p, _) => new TextBlock { Text = p is null ? "" : $"{(p.Name.Length == 0 ? p.ContentType : p.Name)} · {(p.ContentId is not null ? "内联 CID" : p.ContentType == "message/rfc822" ? "嵌入邮件" : "附件")}" });
        _attachments.SelectionChanged += (_, _) => { _savedAttachment = null; if (_attachmentOpen is not null) _attachmentOpen.IsEnabled = false; };
        attachmentPanel.Children.Add(_attachments); attachmentPanel.Children.Add(new TextBlock { Text = "保存目录（重名自动另存，不覆盖已有文件）" });
        AutomationProperties.SetAutomationId(_attachmentDirectory, "mail-attachment-directory"); AutomationProperties.SetName(_attachmentDirectory, "附件保存目录"); attachmentPanel.Children.Add(_attachmentDirectory);
        var attachmentActions = new WrapPanel { Orientation = Orientation.Horizontal };
        attachmentActions.Children.Add(Button("受限预览", "mail-attachment-preview", PreviewAttachment));
        attachmentActions.Children.Add(Button("保存附件", "mail-attachment-save", SaveAttachment));
        attachmentActions.Children.Add(Button("取消保存", "mail-attachment-cancel", () => { _savingAttachment?.Cancel(); return Task.CompletedTask; }));
        _attachmentOpen = Button("确认打开已保存附件", "mail-attachment-open", () =>
        {
            if (_savedAttachment is { CanOpenExternally: true } saved) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(saved.Path) { UseShellExecute = true });
            return Task.CompletedTask;
        }); _attachmentOpen.IsEnabled = false; attachmentActions.Children.Add(_attachmentOpen);
        attachmentPanel.Children.Add(attachmentActions); footer.Children.Insert(1, new Expander { Header = "附件", Content = attachmentPanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        var content = new Grid { ColumnDefinitions = new("2*,3*"), ColumnSpacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        _readingContent.Content = _body; content.Children.Add(_messages); Grid.SetColumn(_readingContent, 1); content.Children.Add(_readingContent);
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
        _reading?.Cancel(); ReleaseHtml(); _body.Text = ""; _parsed = null; _raw = null; _messageId = null; _remoteExplanation.Text = ""; _attachments.ItemsSource = null; _savedAttachment = null; _attachmentOpen.IsEnabled = false; _status.Text = "正在加载邮件列表…";
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
            ReleaseHtml(); _parsed = null; _raw = null;
            _body.Text = "正在读取…";
            var raw = await _reader.Store.Read(summary.Id, cancellation.Token);
            var mail = await _reader.Parser.Parse(raw, cancellation.Token, Encodings[Math.Max(0, _encoding.SelectedIndex)]);
            if (_disposed || cancellation.IsCancellationRequested) return;
            _parsed = mail; _raw = raw; _messageId = summary.Id;
            _attachments.ItemsSource = (mail.Parts ?? []).Where(p => p.Attachment || p.ContentId is not null || p.Name.Length > 0 || p.ContentType == "message/rfc822").ToArray();
            _attachments.SelectedIndex = _attachments.ItemCount > 0 ? 0 : -1; _savedAttachment = null; _attachmentOpen.IsEnabled = false;
            await PreviewRemote(); ShowContent();
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
            var report = await _exporters[_exportFormat.SelectedIndex].Export(new(selected, _exportDirectory.Text), new Progress<ExportItemResult>(r => { if (!_disposed && ReferenceEquals(_exporting, cancellation)) _status.Text = "导出中：" + r.Detail; }), cancellation.Token);
            if (!_disposed) _status.Text = string.Join("\n", report.Items.Select(r => r.Detail + (r.Path is null ? "" : " " + r.Path)));
        }
        finally { _exporting = null; }
    }

    private void ShowContent()
    {
        if (_parsed is not { } mail || _raw is null) return;
        if (_mode.SelectedIndex == 3) { _ = Guard(ShowHtml); return; }
        ReleaseHtml();
        var warnings = mail.Warnings?.Count > 0 ? "阅读提示：" + string.Join("；", mail.Warnings) + "\n\n" : "";
        _body.Text = _mode.SelectedIndex switch
        {
            1 => warnings + string.Join("\n", (mail.Parts ?? []).Select(p => new string(' ', p.Depth * 2) + $"{p.Path} · {p.ContentType} · {p.Name}{(p.Attachment ? " · 附件" : "")}{(p.ContentId is null ? "" : " · CID:" + p.ContentId)}")),
            2 => (_raw.Length > 128 * 1024 ? "原文预览显示前 128 KiB；完整原始字节保存在工作空间。\n\n" : "") + Encoding.UTF8.GetString(_raw.AsSpan(0, Math.Min(_raw.Length, 128 * 1024))),
            _ => warnings + $"{mail.Subject}\n发件人：{mail.From}\n收件人：{mail.To}\n抄送：{mail.Cc}\n日期：{mail.Date:yyyy-MM-dd HH:mm zzz}\n\n{mail.Text}"
        };
    }

    private async Task ShowHtml()
    {
        if (_reader.Html is null || _parsed is not { } mail) return;
        ReleaseHtml(); var revision = _htmlRevision;
        if (mail.Html.Length == 0) { _body.Text = "此邮件没有 HTML 正文，请切换纯文本。"; return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _htmlCancellation = cancellation;
        try
        {
        var baseUri = "https://mail.mymailbox.invalid/" + Guid.NewGuid().ToString("N") + "/";
        var document = await _reader.Html.Render(mail, baseUri, cancellation: cancellation.Token);
        var loadResults = "";
        if (_reader.Remote is { } remote && _messageId is { } messageId && document.RemoteResources.Count > 0)
        {
            var context = new RemoteContentContext(messageId, mail.From);
            if ((await remote.Preview(context, cancellation.Token)).MatchingRules.Length > 0)
            {
                var loaded = await remote.Load(context, document.RemoteResources, cancellation.Token);
                document = await _reader.Html.Render(mail, baseUri, loaded.Resources, cancellation.Token); loadResults = string.Join("；", loaded.Results);
            }
        }
        if (_disposed || revision != _htmlRevision || _mode.SelectedIndex != 3 || !ReferenceEquals(mail, _parsed)) return;
        _htmlView = new SecureHtmlView(baseUri + "index.html", document);
        _htmlView.ExternalLink += uri => { _pendingLink = uri; _linkPrompt.Text = "即将在浏览器打开：" + uri.AbsoluteUri; };
        _htmlView.Failed += error => { if (!_disposed) _status.Text = error; };
        _htmlView.DocumentLoaded += () => { if (!_disposed) _status.Text = "HTML 已加载。" + (loadResults.Length > 0 ? loadResults : string.Join("；", document.Warnings)); };
        _readingContent.Content = _htmlView;
        }
        finally { if (ReferenceEquals(_htmlCancellation, cancellation)) _htmlCancellation = null; }
    }
    private async Task PreviewRemote()
    {
        if (_reader.Remote is null || _parsed is not { } mail || _messageId is not { } id) return;
        var preview = await _reader.Remote.Preview(new(id, mail.From), _lifetime.Token);
        if (_disposed || !ReferenceEquals(mail, _parsed)) return;
        _remoteExplanation.Text = $"发件地址：{(preview.Sender.Length == 0 ? "无唯一有效地址" : preview.Sender)}\n精确域名：{preview.Domain}\n" +
            (preview.MatchingRules.Length == 0 ? "当前：默认阻止。" : "生效规则：" + string.Join("；", preview.MatchingRules.Select(r => r.Scope == RemoteContentScope.Message ? "本封" : r.Value)));
    }
    private async Task SaveAttachment()
    {
        if (_reader.Attachments is null || _messageId is not { } id || _attachments.SelectedItem is not MimePartInfo part) throw new InvalidDataException("请选择附件。");
        if (string.IsNullOrWhiteSpace(_attachmentDirectory.Text)) throw new InvalidDataException("请填写附件保存目录。");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _savingAttachment = cancellation;
        try
        {
            var saved = await _reader.Attachments.Save(id, part.Path, _attachmentDirectory.Text, new Progress<long>(bytes => { if (!_disposed) _status.Text = $"已保存 {bytes:N0} 字节…"; }), cancellation.Token);
            if (_disposed || id != _messageId || !ReferenceEquals(part, _attachments.SelectedItem)) return;
            _savedAttachment = saved; _attachmentOpen.IsEnabled = saved.CanOpenExternally; _status.Text = saved.Notice + "\n" + saved.Path;
        }
        finally { _savingAttachment = null; }
    }
    private async Task PreviewAttachment()
    {
        if (_reader.Attachments is null || _messageId is not { } id || _attachments.SelectedItem is not MimePartInfo part) throw new InvalidDataException("请选择附件。");
        var preview = await _reader.Attachments.Preview(id, part.Path, _lifetime.Token);
        if (_disposed || id != _messageId || !ReferenceEquals(part, _attachments.SelectedItem)) return;
        ReleaseHtml();
        if (preview.Kind == "image" && preview.Resource is { } resource)
        {
            var baseUri = "https://mail.mymailbox.invalid/" + Guid.NewGuid().ToString("N") + "/";
            var mail = new ParsedMail("", "", "", DateTimeOffset.UtcNow, "", Html: "<img src=\"cid:preview\">", InlineResources: [resource]);
            var document = await _reader.Html!.Render(mail, baseUri, cancellation: _lifetime.Token);
            if (_disposed || id != _messageId) return;
            _htmlView = new SecureHtmlView(baseUri + "index.html", document); _htmlView.Failed += error => _status.Text = error; _readingContent.Content = _htmlView;
        }
        else _body.Text = preview.Text;
        _status.Text = "附件受限预览；返回邮件可重新选择阅读模式。";
    }
    private async Task SetRemote(bool allow)
    {
        if (_reader.Remote is null || _parsed is not { } mail || _messageId is not { } id) throw new InvalidDataException("请先打开一封邮件。");
        await _reader.Remote.SetRule(new(id, mail.From), (RemoteContentScope)_remoteScope.SelectedIndex, allow, _lifetime.Token);
        await PreviewRemote();
        if (_mode.SelectedIndex != 3) _mode.SelectedIndex = 3; else await ShowHtml();
    }
    private void ReleaseHtml()
    {
        _htmlCancellation?.Cancel();
        _htmlRevision++; _htmlView?.Dispose(); _htmlView = null; _readingContent.Content = _body; _pendingLink = null; _linkPrompt.Text = "";
    }

    public void Dispose() { if (_disposed) return; _disposed = true; ReleaseHtml(); _reading?.Cancel(); _exporting?.Cancel(); _savingAttachment?.Cancel(); _lifetime.Cancel(); _lifetime.Dispose(); _messages.ItemsSource = null; _folders.ItemsSource = null; _raw = null; _parsed = null; }
}
