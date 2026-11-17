using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class ExportView : UserControl, IDisposable
{
    private readonly ExportSource _source;
    private readonly IReadOnlyList<IMessageExporter> _exporters;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private readonly HashSet<Guid> _accounts = [], _folders = [];
    private readonly StackPanel _choices = new() { Spacing = 6 };
    private readonly StackPanel _form = new() { Spacing = 10 };
    private readonly TextBox _subject = new(), _directory = new(), _extensions = new(), _template = new() { Text = "{subject}-{name}" };
    private readonly ComboBox _format = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _addresses = new() { Content = "包含发件人、收件人和抄送地址（敏感信息）" };
    private readonly CheckBox _attachmentNames = new() { Content = "包含附件名称（可能有敏感信息）" }, _inline = new() { Content = "附件导出包含内联 CID 内容" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _results = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160 };
    private readonly Button _execute;
    private ExportItem[]? _preview;
    private bool _disposed;

    public ExportView(ExportSource source, IReadOnlyList<IMessageExporter> exporters)
    {
        _source = source; _exporters = exporters;
        _format.ItemsSource = exporters.Select(e => e.Name).ToArray(); _format.SelectedIndex = exporters.Count > 0 ? 0 : -1;
        _form.Children.Add(new TextBlock { Text = "批量导出", FontSize = 28, FontWeight = FontWeight.SemiBold });
        _form.Children.Add(new TextBlock { Text = "选择账户的全部目录，或单独选择目录（不自动包含子目录）。先预览邮件范围，再导出；结果按邮件或附件逐项显示。", TextWrapping = TextWrapping.Wrap });
        _form.Children.Add(new Expander { Header = "1 · 选择账户与目录", Content = _choices, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch });
        Add("主题包含（留空为全部）", _subject, "export-subject");
        _subject.TextChanged += (_, _) => Invalidate();
        _form.Children.Add(Action("预览范围", "export-preview", Preview));
        Add("2 · 导出格式", _format, "export-format");
        var options = new StackPanel { Spacing = 8 };
        options.Children.Add(new TextBlock { Text = "CSV / JSON 默认只包含稳定 ID、主题、时间和原文大小；不导出正文或账户凭据。", TextWrapping = TextWrapping.Wrap });
        options.Children.Add(_addresses); options.Children.Add(_attachmentNames);
        options.Children.Add(new TextBlock { Text = "附件扩展名（逗号分隔，留空为全部，例如 pdf,jpg）" }); options.Children.Add(_extensions);
        options.Children.Add(new TextBlock { Text = "附件命名模板：{subject} 主题、{name} 原文件名、{id} 邮件 ID。自动保留扩展名，重名另存。", TextWrapping = TextWrapping.Wrap }); options.Children.Add(_template); options.Children.Add(_inline);
        _form.Children.Add(new Expander { Header = "格式选项与敏感字段", Content = options, HorizontalAlignment = HorizontalAlignment.Stretch });
        AutomationProperties.SetAutomationId(_addresses, "export-addresses"); AutomationProperties.SetAutomationId(_attachmentNames, "export-attachment-names");
        AutomationProperties.SetAutomationId(_extensions, "export-extensions"); AutomationProperties.SetName(_extensions, "附件扩展名");
        AutomationProperties.SetAutomationId(_template, "export-template"); AutomationProperties.SetName(_template, "附件命名模板");
        Add("3 · 保存目录", _directory, "export-directory");
        _form.Children.Add(Action("选择保存目录…", "export-browse", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new() { Title = "选择导出目录" });
            if (folders.Count > 0) _directory.Text = folders[0].Path.LocalPath;
        }));
        _execute = Action("按预览导出", "export-execute", Export); _execute.IsEnabled = false; _form.Children.Add(_execute);
        var footer = new StackPanel { Spacing = 8 }; footer.Children.Add(Action("取消当前操作", "export-cancel", () => { _operation?.Cancel(); return Task.CompletedTask; }));
        AutomationProperties.SetAutomationId(_status, "export-status"); footer.Children.Add(_status);
        AutomationProperties.SetAutomationId(_results, "export-results"); AutomationProperties.SetName(_results, "逐项导出结果"); _form.Children.Add(_results);
        var layout = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer); layout.Children.Add(new ScrollViewer { Content = _form }); Content = layout;
        AttachedToVisualTree += async (_, _) => await Guard(Load);
    }
    private void Add(string label, Control input, string id)
    { _form.Children.Add(new TextBlock { Text = label }); _form.Children.Add(input); AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label); }
    private Button Action(string text, string id, Func<Task> run)
    { var button = new Button { Content = text }; AutomationProperties.SetAutomationId(button, id); button.Click += async (_, _) => await Guard(run); return button; }
    private async Task Guard(Func<Task> run)
    {
        try { await run(); } catch (OperationCanceledException) { if (!_disposed) _status.Text = "操作已取消。"; }
        catch (Exception error) { if (!_disposed) _status.Text = "操作未完成：" + error.Message; }
    }
    private void Invalidate() { _preview = null; _execute.IsEnabled = false; _status.Text = "范围已改变，请重新预览。"; }
    private async Task Load()
    {
        foreach (var kind in new[] { SelectionKind.Account, SelectionKind.Folder })
        {
            var choices = await _source.Selections.Query(new(kind), _lifetime.Token);
            if (_disposed) return;
            foreach (var choice in choices)
            {
                var id = kind == SelectionKind.Account ? choice.Reference.AccountId : choice.Reference.FolderId!.Value;
                var selected = kind == SelectionKind.Account ? _accounts : _folders;
                var check = new CheckBox { Content = (kind == SelectionKind.Account ? "全部目录 · " : "单个目录 · ") + choice.Path, IsEnabled = choice.Available };
                AutomationProperties.SetAutomationId(check, "export-scope-" + kind + "-" + id);
                check.IsCheckedChanged += (_, _) => { if (check.IsChecked == true) selected.Add(id); else selected.Remove(id); Invalidate(); };
                _choices.Children.Add(check);
            }
        }
    }
    private async Task Preview()
    {
        if (_operation is not null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; _form.IsEnabled = false;
        try
        {
            _preview = await ExportScope.Collect(_source.Store, _accounts.ToArray(), _folders.ToArray(), _subject.Text ?? "", operation.Token);
            if (_disposed) return;
            _status.Text = $"预览：{_accounts.Count} 个账户、{_folders.Count} 个单独目录，去重后 {_preview.Length} 封。导出使用这些稳定邮件 ID；预览后新增的邮件不自动加入。";
            _results.Text = string.Join("\n", _preview.Take(200).Select(i => i.Subject)) + (_preview.Length > 200 ? "\n…仅展示前 200 封，导出包含全部预览结果。" : "");
            _execute.IsEnabled = _preview.Length > 0;
        }
        finally { _operation = null; _form.IsEnabled = true; }
    }
    private async Task Export()
    {
        if (_operation is not null || _preview is null || _format.SelectedIndex < 0) return;
        if (string.IsNullOrWhiteSpace(_directory.Text)) throw new ArgumentException("请选择保存目录。");
        var fields = new List<string> { "subject", "date", "size" }; if (_addresses.IsChecked == true) fields.AddRange(["from", "to", "cc"]); if (_attachmentNames.IsChecked == true) fields.Add("attachments");
        var request = new ExportRequest(_preview, _directory.Text) { Options = new() { MetadataFields = fields.ToArray(),
            AttachmentExtensions = (_extensions.Text ?? "").Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AttachmentNameTemplate = _template.Text ?? "{subject}-{name}", IncludeInline = _inline.IsChecked == true } };
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; _form.IsEnabled = false;
        try
        {
            var report = await _exporters[_format.SelectedIndex].Export(request, new Progress<ExportItemResult>(r => { if (!_disposed) _status.Text = "导出中：" + r.Detail; }), operation.Token);
            if (_disposed) return;
            _results.Text = string.Join("\n", report.Items.Select(r => $"{r.Status} · {r.MessageId} {r.Part}\n{r.Path ?? r.Detail}"));
            _status.Text = $"导出结束：{report.Items.Count(i => i.Status is "success" or "existing")} 成功，{report.Items.Count(i => i.Status == "failed")} 失败，{report.Items.Count(i => i.Status == "canceled")} 取消，{report.Items.Count(i => i.Status == "skipped")} 跳过。";
        }
        finally { _operation = null; _form.IsEnabled = true; }
    }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _operation?.Cancel(); _lifetime.Dispose(); }
}
