using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using MyMailBox.Contracts;
using System.Globalization;

namespace MyMailBox.Desktop;

internal sealed partial class MailSearchView
{
    private sealed record Branch(ConditionNode Node, string Label);
    private readonly ListBox _branches = new() { MaxHeight = 158 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _scopes = new(), _saved = new();
    private readonly TextBox _savedName = new();
    private readonly CalendarDatePicker _dateValue = new();
    private readonly NumericUpDown _sizeValue = new() { Minimum = 0, Maximum = long.MaxValue, Increment = 1, FormatString = "0" };
    private readonly ComboBox _boolValue = new() { ItemsSource = new[] { "是", "否" }, SelectedIndex = 0 };
    private string? _drawn;
    private Guid? _loaded;
    private SearchLibrary? _library;

    private void AddBuilder(StackPanel panel)
    {
        _branches.ItemTemplate = new FuncDataTemplate<Branch>((b, _) => new TextBlock { Text = b?.Label });
        Field(panel, "组合条件（选择要修改的项）", _branches, "search-branches");
        var edit = new WrapPanel();
        Action(edit, "从表达式载入", "search-parse", () => { Draw(); return Task.CompletedTask; });
        foreach (var (label, action) in new[] { ("使用字段", "set"), ("并且", "and"), ("或者", "or"), ("取反", "not"), ("移除此项", "remove") })
            Action(edit, label, "search-edit-" + action, () => { Edit(action); return Task.CompletedTask; });
        panel.Children.Add(edit);
        AutomationProperties.SetName(_summary, "条件摘要"); panel.Children.Add(_summary);
        _expression.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { Draw(); e.Handled = true; } };
        _expression.TextChanged += (_, _) =>
        {
            if (_drawn == _expression.Text) return;
            _branches.ItemsSource = null; _summary.Text = "表达式已更改。按 Ctrl+Enter 查看条件摘要。";
        };
        _scopes.ItemTemplate = new FuncDataTemplate<SearchScope>((s, _) => new TextBlock { Text = s?.Name });
        _saved.ItemTemplate = new FuncDataTemplate<SavedSearch>((s, _) => new TextBlock { Text = s?.Name });
        Field(panel, "搜索范围", _scopes, "search-scope");
        Field(panel, "已保存搜索", _saved, "search-saved");
        Field(panel, "搜索名称", _savedName, "search-name");
        var buttons = new WrapPanel();
        Action(buttons, "载入所选搜索", "search-load", LoadSaved);
        Action(buttons, "另存为新搜索", "search-save", () => SaveSearch(false));
        Action(buttons, "更新已载入搜索", "search-update", () => SaveSearch(true)); panel.Children.Add(buttons);
    }

    private FieldCriterion? Draft()
    {
        if (_field.SelectedItem is not SearchField field || _match.SelectedItem is not SearchMatch match) return null;
        var value = field.Kind switch
        {
            SearchKind.LocalDate => _dateValue.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            SearchKind.Number => _sizeValue.Value?.ToString("0", CultureInfo.InvariantCulture),
            SearchKind.Boolean => _boolValue.SelectedItem as string,
            _ => _value.Text
        };
        return match == SearchMatch.Exists || !string.IsNullOrWhiteSpace(value) ? new FieldCriterion(field.Id, match, value ?? "") { Operator = field.Operator } : null;
    }

    private ConditionNode? Draw()
    {
        var original = _expression.Text ?? "";
        try
        {
            var root = string.IsNullOrWhiteSpace(original) ? null : ConditionTree.Parse(original).Root;
            var branches = new List<Branch>();
            void Visit(ConditionNode node, int depth)
            {
                if (node.Criterion is { } c && !_service.Fields.Any(f => f.Id == c.Field && (c.Operator is null || c.Operator == f.Operator)))
                    throw new InvalidDataException("字段 " + c.Field + " 无法在条件编辑器显示，已保留原表达式。");
                var label = node.Criterion is not null ? ConditionEditor.Describe(node, _service.Fields) :
                    node.Logic switch { ConditionLogic.And => "同时满足", ConditionLogic.Or => "满足任一", _ => "取反" };
                branches.Add(new(node, new string('　', depth) + label)); foreach (var child in node.Children) Visit(child, depth + 1);
            }
            if (root is not null) Visit(root, 0);
            _branches.ItemsSource = branches; _branches.SelectedIndex = branches.Count == 0 ? -1 : 0;
            _summary.Text = root is null ? "可选一个字段开始组合。" : ConditionEditor.Describe(root, _service.Fields);
            _drawn = original; return root;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException)
        {
            _drawn = null; _branches.ItemsSource = null; _summary.Text = e.Message; return null;
        }
    }

    private void Edit(string action)
    {
        if (_drawn != (_expression.Text ?? "")) Draw();
        if (_drawn is null) return;
        var root = _branches.Items.OfType<Branch>().FirstOrDefault()?.Node;
        var target = (_branches.SelectedItem as Branch)?.Node ?? root;
        ConditionNode? next;
        if (action is "set" or "and" or "or")
        {
            var leaf = ConditionEditor.Leaf(Draft() ?? throw new InvalidDataException("请先填写字段和条件值。"));
            next = target is null || action == "set" ? leaf : new(action == "and" ? ConditionLogic.And : ConditionLogic.Or, null, [target, leaf]);
        }
        else if (target is null) return;
        else next = action == "remove" ? null : new(ConditionLogic.Not, null, [target]);
        var result = root is null ? next : ConditionEditor.Replace(root, target!, next);
        _expression.Text = result is null ? "" : ConditionEditor.Expression(result); Draw();
    }

    private async Task RefreshLibrary(Guid? selected = null)
    {
        var old = _scopes.SelectedItem as SearchScope;
        _library = await _service.Library(_stop.Token); _saved.ItemsSource = _library.Saved; _scopes.ItemsSource = _library.Scopes;
        _scopes.SelectedItem = _library.Scopes.FirstOrDefault(s => s.Account == old?.Account && s.Folder == old?.Folder) ?? _library.Scopes[0];
        _saved.SelectedItem = _library.Saved.FirstOrDefault(s => s.Id == selected);
    }

    private SavedSearch CurrentDefinition(Guid id)
    {
        var scope = _scopes.SelectedItem as SearchScope;
        if (scope is null && _library is not null) throw new InvalidDataException("原搜索范围已失效，请选择替代账户或目录。");
        var expression = _expression.Text ?? "";
        if (string.IsNullOrWhiteSpace(expression) && Draft() is { } criterion) expression = ConditionEditor.Expression(ConditionEditor.Leaf(criterion));
        return new(id, _savedName.Text ?? "", expression, (SearchOrder)_order.SelectedIndex, scope?.Account, scope?.Folder);
    }

    private async Task SaveSearch(bool replace)
    {
        var definition = CurrentDefinition(replace ? _loaded ?? throw new InvalidDataException("请先载入要更新的搜索。") : Guid.NewGuid());
        await _service.Save(definition, _stop.Token); await RefreshLibrary(definition.Id); _loaded = definition.Id; _status.Text = "已保存搜索「" + definition.Name + "」。";
    }

    private async Task LoadSaved()
    {
        if (_saved.SelectedItem is not SavedSearch search) return;
        await RefreshLibrary(search.Id);
        _loaded = search.Id; _savedName.Text = search.Name; _expression.Text = search.Expression; _order.SelectedIndex = (int)search.Order;
        _field.SelectedIndex = 0; _value.Text = ""; Draw();
        _scopes.SelectedItem = _library!.Scopes.FirstOrDefault(s => s.Account == search.Account && s.Folder == search.Folder);
        if (_scopes.SelectedItem is null) { _results.ItemsSource = null; _status.Text = "搜索范围已失效，请重新选择并更新已载入搜索。"; return; }
        await RunInput();
    }
}
