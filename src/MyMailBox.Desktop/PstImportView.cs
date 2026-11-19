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
    private readonly TextBox _file = new();
    private readonly ComboBox _formats = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _parameters = new() { Spacing = 4 };
    private readonly Dictionary<string,TextBox> _inputs = [];
    private readonly ComboBox _targets = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _sessions = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _recovery = new() { Spacing = 8, IsVisible = false };
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
        panel.Children.Add(new TextBlock { Text = "从文件导入邮件", FontSize = 26 });
        panel.Children.Add(new TextBlock { Text = "先预览目录，再导入到本地邮箱。每封邮件单独保存，取消后仍可阅读已导入的邮件。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_form);
        _sessions.ItemTemplate = new FuncDataTemplate<PstSession>((s, _) => new TextBlock { Text = s == null ? "" : $"{Path.GetFileName(s.File)} → {s.TargetName}（已保存 {s.Committed} 封）" });
        _form.Children.Add(_recovery);
        Field("未完成的导入", "pst-sessions", _sessions, _recovery);
        var resume = new Button { Content = "恢复所选导入" };
        AutomationProperties.SetAutomationId(resume, "pst-resume");
        resume.Click += async (_, _) => await Run(async token =>
        {
            if (_sessions.SelectedItem is not PstSession saved) throw new InvalidDataException("请选择未完成的导入。");
            InvalidatePreview();
            var format = _service.Formats.SingleOrDefault(f => f.Id == saved.Format && f.Version == saved.Version)
                ?? throw new InvalidDataException("原导入器或对应版本未启用，请先在插件管理中恢复。");
            _formats.SelectedItem = (_formats.ItemsSource as IEnumerable<ImportFormat>)?.Single(f => f.Id == format.Id);
            foreach (var input in _inputs) input.Value.Text = saved.Parameters.GetValueOrDefault(input.Key);
            _file.Text = saved.File;
            _targets.SelectedItem = (_targets.ItemsSource as IEnumerable<LocalMailboxInfo>)?.SingleOrDefault(m => m.Id == saved.Target);
            var preview = await _service.PreviewFile(format.Id, saved.File, saved.Parameters, token);
            if (preview.Fingerprint != saved.Fingerprint) throw new InvalidDataException("源文件已变化。请重新预览并确认目标邮箱。");
            _preview = preview;
            _reportPath = saved.Report;
            _folders.ItemsSource = preview.Folders.Select(f => $"{f.Path}（{f.Items} 项）").ToArray();
            _status.Text = $"已恢复到 {saved.TargetName}，此前已保存 {saved.Committed} 封。点击开始导入可继续未处理项目并重试失败项。";
        });
        _recovery.Children.Add(resume);
        _formats.ItemTemplate = new FuncDataTemplate<ImportFormat>((f, _) => new TextBlock { Text = f?.Name });
        Field("文件格式", "import-format", _formats);
        Field("源文件", "pst-file", _file);
        var browse = new Button { Content = "选择文件…" };
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            if (_formats.SelectedItem is not ImportFormat format) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "选择源文件", FileTypeFilter = [new(format.Name) { Patterns = [format.Pattern] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _file.Text = path;
        };
        _form.Children.Add(browse);
        _form.Children.Add(_parameters);
        _formats.SelectionChanged += (_, _) =>
        {
            InvalidatePreview();
            _inputs.Clear(); _parameters.Children.Clear();
            if (_formats.SelectedItem is not ImportFormat format) return;
            foreach (var parameter in format.Parameters)
            {
                var box = new TextBox { Text = parameter.Default };
                _inputs.Add(parameter.Id, box);
                box.TextChanged += (_, _) => InvalidatePreview();
                _parameters.Children.Add(new TextBlock { Text = parameter.Label, TextWrapping = TextWrapping.Wrap });
                AutomationProperties.SetAutomationId(box, "import-option-" + parameter.Id);
                AutomationProperties.SetName(box, parameter.Label);
                _parameters.Children.Add(box);
            }
        };
        _targets.ItemTemplate = new FuncDataTemplate<LocalMailboxInfo>((m, _) => new TextBlock { Text = m?.Name });
        Field("目标本地邮箱", "pst-target", _targets);
        var preview = new Button { Content = "预览目录" };
        AutomationProperties.SetAutomationId(preview, "pst-preview");
        preview.Click += async (_, _) => await Run(async token =>
        {
            InvalidatePreview();
            if (_formats.SelectedItem is not ImportFormat format) throw new InvalidDataException("请选择文件格式。");
            _status.Text = "正在读取目录…";
            _preview = await _service.PreviewFile(format.Id, _file.Text ?? "", _inputs.ToDictionary(p => p.Key, p => p.Value.Text ?? ""), token);
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
        var feedback = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 10, 0, 0) };
        feedback.Children.Add(buttons);
        AutomationProperties.SetAutomationId(_status, "pst-status");
        feedback.Children.Add(_status);
        var layout = new DockPanel();
        DockPanel.SetDock(feedback, Dock.Bottom);
        layout.Children.Add(feedback);
        layout.Children.Add(new ScrollViewer { Content = panel });
        Content = layout;
        _file.TextChanged += (_, _) => InvalidatePreview();
        _targets.SelectionChanged += (_, _) => UpdateActions();
        _start.Click += async (_, _) => await Run(async token =>
        {
            if (_preview == null || _targets.SelectedItem is not LocalMailboxInfo target) return;
            _reportPath = null;
            _status.Text = "正在核对源文件…";
            var progress = new Progress<PstProgress>(p => { if (_operation != null && !token.IsCancellationRequested) _status.Text = Summary(p) + "\n" + p.Current; });
            PstResult result;
            try { result = await _service.Import(_preview, target.Id, progress, token); }
            finally { if (!_disposed) await LoadSessions(); }
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
            _formats.ItemsSource = service.Formats;
            _formats.SelectedIndex = 0;
            _targets.ItemsSource = targets;
            _targets.SelectedIndex = targets.Count > 0 ? 0 : -1;
            await LoadSessions();
            if (targets.Count == 0) _status.Text = "请先在“本地邮箱管理”中打开或创建邮箱。";
        });
    }

    private async Task LoadSessions()
    {
        var sessions = await _service.Pending(CancellationToken.None);
        if (_disposed) return;
        _sessions.ItemsSource = sessions;
        _sessions.SelectedIndex = sessions.Count > 0 ? 0 : -1;
        _recovery.IsVisible = sessions.Count > 0;
    }

    private static string Summary(PstProgress p) => $"已导入 {p.Imported} 封，已存在 {p.Duplicates} 封，跳过 {p.Skipped} 项，失败 {p.Failed} 项";
    private void Field(string label, string id, Control control, StackPanel? parent = null)
    {
        var container = parent ?? _form;
        container.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, label);
        container.Children.Add(control);
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
