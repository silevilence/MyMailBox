using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class PstImportView : UserControl, IDisposable
{
    private readonly IPstImport _service;
    private readonly StackPanel _form = new() { Spacing = 8 };
    private readonly TextBox _file = new(), _codePage = new();
    private readonly ComboBox _targets = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _folders = new() { Height = 180 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "开始导入", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "取消", IsEnabled = false };
    private readonly Button _report = new() { Content = "查看逐项报告", IsEnabled = false };
    private CancellationTokenSource? _operation;
    private PstPreview? _preview;
    private string? _reportPath;
    private bool _disposed;

    public PstImportView(IPstImport service)
    {
        _service = service;
        var panel = new StackPanel { Spacing = 12, MaxWidth = 800 };
        panel.Children.Add(new TextBlock { Text = "导入 Outlook 数据文件", FontSize = 26 });
        panel.Children.Add(new TextBlock { Text = "先预览目录，再导入到本地邮箱。每封邮件单独保存，取消后仍可阅读已导入的邮件。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_form);
        Field("PST 文件", "pst-file", _file);
        var browse = new Button { Content = "选择 PST 文件…" };
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "选择 Outlook 数据文件", FileTypeFilter = [new("Outlook 数据文件") { Patterns = ["*.pst"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _file.Text = path;
        };
        _form.Children.Add(browse);
        Field("旧版 ANSI 文件的源代码页（可留空，简体中文通常为 936）", "pst-codepage", _codePage);
        _targets.ItemTemplate = new FuncDataTemplate<LocalMailboxInfo>((m, _) => new TextBlock { Text = m?.Name });
        Field("目标本地邮箱", "pst-target", _targets);
        var preview = new Button { Content = "预览目录" };
        AutomationProperties.SetAutomationId(preview, "pst-preview");
        preview.Click += async (_, _) => await Run(async token =>
        {
            InvalidatePreview();
            int? cp = string.IsNullOrWhiteSpace(_codePage.Text) ? null : int.TryParse(_codePage.Text.Trim(), out var number) && number > 0 ? number : throw new InvalidDataException("请填写有效的代码页编号。");
            _status.Text = "正在读取目录…";
            _preview = await _service.Preview(_file.Text ?? "", cp, token);
            _folders.ItemsSource = _preview.Folders.Select(f => $"{f.Path}（{f.Items} 项）").ToArray();
            _status.Text = $"共 {_preview.Folders.Length} 个目录、{_preview.Folders.Sum(f => (long)f.Items)} 个项目。请核对目标邮箱后开始。";
        });
        _form.Children.Add(preview);
        Field("文件内的目录与项目数", "pst-folders", _folders);
        var buttons = new WrapPanel();
        foreach (var (button, id) in new[] { (_start, "pst-start"), (_cancel, "pst-cancel"), (_report, "pst-report") })
        {
            AutomationProperties.SetAutomationId(button, id);
            button.Margin = new Avalonia.Thickness(0, 0, 8, 0);
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        AutomationProperties.SetAutomationId(_status, "pst-status");
        panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel };
        _file.TextChanged += (_, _) => InvalidatePreview();
        _codePage.TextChanged += (_, _) => InvalidatePreview();
        _targets.SelectionChanged += (_, _) => UpdateActions();
        _start.Click += async (_, _) => await Run(async token =>
        {
            if (_preview == null || _targets.SelectedItem is not LocalMailboxInfo target) return;
            _reportPath = null;
            _status.Text = "正在核对源文件…";
            var progress = new Progress<PstProgress>(p => { if (_operation != null && !token.IsCancellationRequested) _status.Text = Summary(p) + "\n" + p.Current; });
            var result = await _service.Import(_preview, target.Id, progress, token);
            _reportPath = result.Report;
            _status.Text = "导入结束。" + Summary(result.Counts) + "。可在“本地邮箱”中按目录阅读。";
        });
        _cancel.Click += (_, _) => _operation?.Cancel();
        _report.Click += (_, _) =>
        {
            if (_reportPath != null) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { _reportPath } });
        };
        AttachedToVisualTree += async (_, _) => await Run(async token =>
        {
            var targets = await service.Targets(token);
            _targets.ItemsSource = targets;
            _targets.SelectedIndex = targets.Count > 0 ? 0 : -1;
            if (targets.Count == 0) _status.Text = "请先在“本地邮箱管理”中打开或创建邮箱。";
        });
    }

    private static string Summary(PstProgress p) => $"已导入 {p.Imported} 封，已存在 {p.Duplicates} 封，跳过 {p.Skipped} 项，失败 {p.Failed} 项";
    private void Field(string label, string id, Control control)
    {
        _form.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, label);
        _form.Children.Add(control);
    }
    private void InvalidatePreview() { _preview = null; _folders.ItemsSource = null; UpdateActions(); }
    private void UpdateActions()
    {
        _start.IsEnabled = _operation == null && _preview != null && _targets.SelectedItem != null;
        _cancel.IsEnabled = _operation != null;
        _report.IsEnabled = _operation == null && _reportPath != null;
    }
    private async Task Run(Func<CancellationToken, Task> work)
    {
        if (_operation != null || _disposed) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _form.IsEnabled = false;
        UpdateActions();
        try { await work(operation.Token); }
        catch (OperationCanceledException) { _status.Text = "已取消。再次导入同一文件时会跳过已保存的邮件。"; }
        catch (Exception e) { _status.Text = "操作未完成：" + e.Message; }
        finally { _operation = null; _form.IsEnabled = true; UpdateActions(); }
    }
    public void Dispose() { _disposed = true; _operation?.Cancel(); }
}
