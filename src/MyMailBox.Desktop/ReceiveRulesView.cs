using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class ReceiveRulesView : UserControl, IDisposable
{
    private readonly IReceiveRules _service;
    private readonly CancellationTokenSource _stop = new();
    private readonly StackPanel _panel = new() { Spacing = 6, MaxWidth = 820 };
    private readonly ComboBox _saved = new(), _scope = new(), _destination = new(), _kind = new();
    private readonly TextBox _name = new(), _condition = new(), _except = new(), _label = new();
    private readonly NumericUpDown _order = new() { Minimum = 0, Maximum = 100000, Increment = 1, FormatString = "0", Value = 100 };
    private readonly CheckBox _enabled = new() { Content = "启用规则", IsChecked = true }, _halt = new() { Content = "命中后停止后续规则" }, _history = new() { Content = "同时处理新导入的历史邮件和初次同步" };
    private readonly ListBox _actions = new() { MaxHeight = 100 }, _runs = new() { MaxHeight = 190 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _trace = new() { MaxHeight=150 };
    private readonly List<RuleAction> _draft = [];
    private ReceiveRule? _editing;
    private RuleDashboard? _dashboard;

    public ReceiveRulesView(IReceiveRules service)
    {
        _service = service; _panel.Children.Add(new TextBlock { Text = "收件规则", FontSize = 28, FontWeight = FontWeight.SemiBold });
        _saved.ItemTemplate = new FuncDataTemplate<ReceiveRule>((r, _) => new TextBlock { Text = r is null ? "" : $"{r.Order} · {r.Name} · {(r.Enabled ? "已启用" : "已停用")}" });
        _scope.ItemTemplate = _destination.ItemTemplate = new FuncDataTemplate<SearchScope>((s, _) => new TextBlock { Text = s?.Name });
        Field("已有规则", _saved, "rule-list");
        var manage = new WrapPanel(); Button(manage, "载入规则", "rule-load", () => { if (_saved.SelectedItem is ReceiveRule r) Edit(r); return Task.CompletedTask; });
        Button(manage, "新规则", "rule-new", () => { Edit(null); return Task.CompletedTask; }); _panel.Children.Add(manage);
        Field("规则名称", _name, "rule-name"); Field("应用范围", _scope, "rule-scope"); Field("执行顺序（较小者优先）", _order, "rule-order");
        Field("匹配条件", _condition, "rule-condition"); Field("例外条件（可留空）", _except, "rule-except");
        _panel.Children.Add(new TextBlock { Text = "例：subject:合同 AND from:example.test。条件语法与高级搜索相同。", TextWrapping = TextWrapping.Wrap });
        _kind.ItemsSource = Enum.GetValues<RuleActionKind>();
        _kind.ItemTemplate = new FuncDataTemplate<RuleActionKind>((k, _) => new TextBlock { Text = ActionName(k) });
        Field("动作", _kind, "rule-kind"); Field("标签名", _label, "rule-label"); Field("移动到", _destination, "rule-target");
        _kind.SelectionChanged += (_, _) => { _label.IsEnabled = _kind.SelectedItem is RuleActionKind.Label; _destination.IsEnabled = _kind.SelectedItem is RuleActionKind.Move; }; _kind.SelectedIndex = 0;
        _scope.SelectionChanged += (_, _) => FilterTargets();
        _actions.ItemTemplate = new FuncDataTemplate<RuleAction>((a, _) => new TextBlock { Text = a is null ? "" : ActionName(a.Kind) + " " + (a.Label ?? _dashboard?.Scopes.FirstOrDefault(s => s.Folder == a.Destination && a.Destination.HasValue)?.Name) });
        var tools = new WrapPanel(); Button(tools, "添加动作", "rule-add-action", () => { AddAction(); return Task.CompletedTask; });
        Button(tools, "移除所选动作", "rule-remove-action", () => { if (_actions.SelectedIndex >= 0) _draft.RemoveAt(_actions.SelectedIndex); ShowActions(); return Task.CompletedTask; }); _panel.Children.Add(tools);
        Field("按顺序执行", _actions, "rule-actions");
        _panel.Children.Add(_enabled); _panel.Children.Add(_halt); _panel.Children.Add(_history);
        var save = new WrapPanel(); Button(save, "保存规则", "rule-save", Save); Button(save, "刷新记录", "rule-refresh", () => Refresh(_editing?.Id)); _panel.Children.Add(save);
        _runs.ItemTemplate = new FuncDataTemplate<RuleRun>((r, _) => new TextBlock { Text = r is null ? "" : $"{r.Name} v{r.Version} · {r.Detail}", TextWrapping = TextWrapping.Wrap });
        Field("最近执行记录", _runs, "rule-log");
        _trace.ItemTemplate = new FuncDataTemplate<RuleActionLog>((a,_) => new TextBlock { Text=a is null ? "" : $"{a.Ordinal+1}. {ActionName(a.Kind)} · {a.Detail} · {a.Operation}", TextWrapping=TextWrapping.Wrap });
        Field("所选记录的动作",_trace,"rule-trace");
        _runs.SelectionChanged += async (_,_) => await Guard(async () =>
        {
            if (_runs.SelectedItem is not RuleRun run) { _trace.ItemsSource = null; return; }
            var items = await service.Actions(run.Id,_stop.Token);
            if (_runs.SelectedItem is RuleRun selected && selected.Id==run.Id) _trace.ItemsSource=items;
        }); AutomationProperties.SetAutomationId(_status, "rule-status"); _panel.Children.Add(_status);
        Content = new ScrollViewer { Content = _panel }; AttachedToVisualTree += async (_, _) => await Guard(async () => { await Refresh(null); Edit(null); });
    }
    private void Field(string label, Control control, string id)
    {
        _panel.Children.Add(new TextBlock { Text = label }); AutomationProperties.SetName(control, label); AutomationProperties.SetAutomationId(control, id); _panel.Children.Add(control);
    }
    private void Button(Panel panel, string title, string id, Func<Task> work)
    {
        var b = new Button { Content = title, Margin = new Avalonia.Thickness(0, 0, 6, 2) }; AutomationProperties.SetAutomationId(b, id);
        b.Click += async (_, _) => await Guard(work); panel.Children.Add(b);
    }
    private async Task Guard(Func<Task> work)
    {
        try { await work(); } catch (OperationCanceledException) { } catch (Exception e) { if (!_stop.IsCancellationRequested) _status.Text = e.Message; }
    }
    private static string ActionName(RuleActionKind k) => k switch { RuleActionKind.Read => "标为已读", RuleActionKind.Unread => "标为未读", RuleActionKind.Star => "加星标", RuleActionKind.Unstar => "取消星标", RuleActionKind.Label => "添加标签", _ => "移动邮件" };
    private void FilterTargets()
    {
        _destination.ItemsSource = _dashboard?.Scopes.Where(s => s.Folder.HasValue && s.Account == (_scope.SelectedItem as SearchScope)?.Account).ToArray();
    }
    private void AddAction()
    {
        if (_kind.SelectedItem is not RuleActionKind kind) return;
        var target = (_destination.SelectedItem as SearchScope)?.Folder;
        if (kind == RuleActionKind.Move && target is null) throw new InvalidDataException("请先选择账户范围和移动目标。");
        _draft.Add(new(kind, kind == RuleActionKind.Label ? _label.Text : null, kind == RuleActionKind.Move ? target : null)); ShowActions();
    }
    private void ShowActions() => _actions.ItemsSource = _draft.ToArray();
    private void Edit(ReceiveRule? rule)
    {
        _editing = rule; _name.Text = rule?.Name ?? ""; _condition.Text = rule?.Condition ?? ""; _except.Text = rule?.Exception ?? "";
        _scope.SelectedItem = _dashboard?.Scopes.FirstOrDefault(s => s.Account == rule?.Account && s.Folder == rule?.Folder);
        _order.Value = rule?.Order ?? 100; _enabled.IsChecked = rule?.Enabled ?? true; _halt.IsChecked = rule?.Stop ?? false; _history.IsChecked = rule?.IncludeHistory ?? false;
        _draft.Clear(); if (rule is not null) _draft.AddRange(rule.Actions); ShowActions();
    }
    private async Task Refresh(Guid? selected)
    {
        var scope = _scope.SelectedItem as SearchScope;
        _dashboard = await _service.Dashboard(_stop.Token); _saved.ItemsSource = _dashboard.Rules; _saved.SelectedItem = _dashboard.Rules.FirstOrDefault(r => r.Id == selected);
        _scope.ItemsSource = _dashboard.Scopes; _scope.SelectedItem = _dashboard.Scopes.FirstOrDefault(s => s.Account == scope?.Account && s.Folder == scope?.Folder);
        _runs.ItemsSource = _dashboard.Runs; FilterTargets();
    }
    private ReceiveRule Draft()
    {
        if (_scope.SelectedItem is not SearchScope scope) throw new InvalidDataException("请选择有效规则范围。");
        return new(_editing?.Id ?? Guid.NewGuid(), _editing?.Version ?? 0, _name.Text ?? "", scope.Account, scope.Folder, _condition.Text ?? "", _except.Text ?? "",
            _draft.ToArray(), (int)(_order.Value ?? 100), _enabled.IsChecked == true, _halt.IsChecked == true, _history.IsChecked == true);
    }
    private async Task Save()
    {
        var rule = await _service.Save(Draft(), _stop.Token); await Refresh(rule.Id); Edit(rule); _status.Text = $"已保存「{rule.Name}」版本 {rule.Version}。";
    }
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
