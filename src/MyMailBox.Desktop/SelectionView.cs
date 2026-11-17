using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class SelectionView : UserControl, IDisposable
{
    private readonly ISelectionService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<SelectionReference> _selected = [];
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "写信 · 发送身份", "整理 · 账户", "整理 · 目录" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _search = new(), _tag = new();
    private readonly CheckBox _recent = new() { Content = "只看最近使用" }, _dynamic = new() { Content = "动态标签集合：以后按标签重新匹配，不固定当前列表" };
    private readonly StackPanel _choices = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _disposed;
    private int _revision;
    private SelectionKind Kind => _kind.SelectedIndex switch { 0 => SelectionKind.Identity, 1 => SelectionKind.Account, _ => SelectionKind.Folder };
    private AccountCapabilities Required => Kind == SelectionKind.Identity ? AccountCapabilities.Send : AccountCapabilities.None;
    private string Slot => Kind == SelectionKind.Identity ? "compose.identity" : "organize." + Kind.ToString().ToLowerInvariant();

    public SelectionView(ISelectionService service)
    {
        _service = service;
        var header = new StackPanel { Spacing = 10 };
        header.Children.Add(new TextBlock { Text = "写信与整理", FontSize = 28, FontWeight = FontWeight.SemiBold });
        header.Children.Add(new TextBlock { Text = "选择发送身份或整理范围。名称变化后仍会找到原来的账户和目录。", TextWrapping = TextWrapping.Wrap });
        Add(header, "用途", _kind, "selection-kind"); Add(header, "搜索名称、地址或目录路径", _search, "selection-search"); Add(header, "标签（逗号分隔；动态集合匹配任一标签）", _tag, "selection-tag");
        AutomationProperties.SetAutomationId(_recent, "selection-recent"); AutomationProperties.SetAutomationId(_dynamic, "selection-dynamic");
        header.Children.Add(_recent); header.Children.Add(_dynamic);
        header.Children.Add(Action("搜索 / 刷新", "selection-refresh", Refresh));
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Action("保存选择", "selection-save", Save)); actions.Children.Add(Action("恢复已保存范围", "selection-restore", Restore)); footer.Children.Add(actions); footer.Children.Add(_status);
        AutomationProperties.SetAutomationId(_status, "selection-status");
        var layout = new DockPanel(); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer); layout.Children.Add(new ScrollViewer { Content = _choices, Margin = new Thickness(0, 12, 0, 0) }); Content = layout;
        _kind.SelectionChanged += async (_, _) => { _selected.Clear(); _dynamic.IsChecked = false; _status.Text = ""; await Guard(Refresh); };
        AttachedToVisualTree += async (_, _) => await Guard(Refresh);
    }
    private static void Add(StackPanel panel, string label, Control input, string id)
    {
        panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(input); AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label);
    }
    private Button Action(string text, string id, Func<Task> execute)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) }; AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await Guard(execute); return button;
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { if (!_disposed) _status.Text = "未完成：" + error.Message; }
    }
    private string[] Tags() => (_tag.Text ?? "").Split([',', '，']).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private async Task Refresh()
    {
        var revision = ++_revision; var kind = Kind;
        var choices = await _service.Query(new(kind, Required, _search.Text ?? "", RecentOnly: _recent.IsChecked == true), _lifetime.Token);
        if (_disposed || revision != _revision) return;
        var tags = Tags(); choices = choices.Where(c => tags.Length == 0 || tags.Any(t => c.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))).ToArray();
        _choices.Children.Clear();
        if (kind == SelectionKind.Folder)
        {
            foreach (var group in choices.GroupBy(c => c.Reference.AccountId))
            {
                var children = new StackPanel { Spacing = 6 }; var all = group.ToArray(); var added = new HashSet<Guid>();
                foreach (var root in all.Where(c => c.ParentId is null || !all.Any(p => p.Reference.FolderId == c.ParentId))) AddFolder(children, root, all, added);
                _choices.Children.Add(new Expander { Header = all[0].Path.Split(" / ")[0], Content = children, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
        }
        else foreach (var choice in choices) _choices.Children.Add(Check(choice));
        if (choices.Count == 0) _choices.Children.Add(new TextBlock { Text = "没有匹配项。可清空筛选，或先在账户管理中添加账户。", TextWrapping = TextWrapping.Wrap });
    }
    private void AddFolder(StackPanel panel, SelectionChoice choice, SelectionChoice[] all, HashSet<Guid> added)
    {
        if (!added.Add(choice.Reference.FolderId!.Value)) return;
        var children = all.Where(c => c.ParentId == choice.Reference.FolderId).ToArray();
        if (children.Length == 0) { panel.Children.Add(Check(choice)); return; }
        var nested = new StackPanel { Spacing = 6, Margin = new Thickness(20, 0, 0, 0) };
        foreach (var child in children) AddFolder(nested, child, all, added);
        var group = new StackPanel(); group.Children.Add(Check(choice)); group.Children.Add(new Expander { Header = "子目录", Content = nested, IsExpanded = true }); panel.Children.Add(group);
    }
    private CheckBox Check(SelectionChoice choice)
    {
        var label = $"{choice.Path} · {(choice.Address.Length > 0 ? choice.Address : "本地邮箱")} · {choice.Provider}";
        if (choice.Role.Length > 0) label += choice.Role switch { "inbox" => " · 收件箱", "sent" => " · 已发送", "drafts" => " · 草稿", "trash" => " · 已删除", _ => "" };
        if (choice.Tags.Length > 0) label += " · " + string.Join(" / ", choice.Tags);
        if (!choice.Available) label += " · " + choice.Reason;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Border { Width = 8, Background = Brush.Parse(choice.Color), CornerRadius = new CornerRadius(2) });
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 570 });
        var box = new CheckBox { Content = row, IsEnabled = choice.Available, IsChecked = _selected.Contains(choice.Reference) };
        AutomationProperties.SetName(box, label); AutomationProperties.SetAutomationId(box, "choice-" + choice.Reference.AccountId + (choice.Reference.FolderId is { } folder ? "-" + folder : ""));
        box.IsCheckedChanged += (_, _) =>
        {
            if (box.IsChecked == true) _selected.Add(choice.Reference); else _selected.Remove(choice.Reference);
            _status.Text = $"已选 {_selected.Count} 项。固定集合仅包含勾选项。";
        };
        return box;
    }
    private async Task Save()
    {
        if (Kind == SelectionKind.Identity && (_dynamic.IsChecked == true || _selected.Count != 1)) throw new InvalidDataException("写信请选择一个固定发送身份。");
        var selection = new SavedSelection(_dynamic.IsChecked == true, Tags(), _dynamic.IsChecked == true ? [] : _selected.ToArray(), Kind, Required);
        await _service.Save(Slot, selection, _lifetime.Token);
        var resolved = await _service.Resolve(selection, _lifetime.Token);
        if (_disposed) return;
        _status.Text = (selection.DynamicTags ? "动态标签集合已保存；执行时重新匹配。当前：" : "固定选择已保存：") + string.Join("；", resolved.Choices.Select(c => c.Path + (c.Address.Length > 0 ? " <" + c.Address + ">" : "")));
    }
    private async Task Restore()
    {
        var selection = await _service.Load(Slot, _lifetime.Token); if (_disposed) return;
        if (selection is null) { _status.Text = "此用途还没有保存过选择。"; return; }
        _selected.Clear(); foreach (var reference in selection.References) _selected.Add(reference);
        _dynamic.IsChecked = selection.DynamicTags; _tag.Text = string.Join(", ", selection.Tags); _search.Text = ""; _recent.IsChecked = false;
        await Refresh(); var resolved = await _service.Resolve(selection, _lifetime.Token); if (_disposed) return;
        _status.Text = $"已恢复{(selection.DynamicTags ? "动态标签集合" : "固定集合")}：{string.Join("；", resolved.Choices.Select(c => c.Path + (c.Available ? "" : "（" + c.Reason + "）")))}";
        if (resolved.Missing.Length > 0) _status.Text += $"；{resolved.Missing.Length} 项引用已失效（账户或目录已删除），请重新选择后保存。";
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); _choices.Children.Clear(); }
}
