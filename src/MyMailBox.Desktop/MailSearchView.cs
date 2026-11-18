using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class MailSearchView : UserControl, IDisposable
{
    private readonly IMailSearch _service;
    private readonly CancellationTokenSource _stop = new();
    private readonly ComboBox _field = new(), _match = new(), _order = new() { ItemsSource = new[] { "最新在前", "最早在前", "最大在前", "主题顺序" }, SelectedIndex = 0 };
    private readonly TextBox _value = new(), _body = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220 };
    private readonly TextBlock _hint = new(), _status = new() { TextWrapping = TextWrapping.Wrap }, _coverage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _results = new() { MinHeight = 120, MaxHeight = 240 };
    private readonly StackPanel _panel = new() { MaxWidth = 900, Spacing = 8 };
    private readonly Button _prev, _next, _read, _folder, _back;
    private SearchRequest _request = new([]);
    private SearchRequest? _previousRequest;
    private string? _backId;
    private int _version;
    private CancellationTokenSource? _operation;

    public MailSearchView(IMailSearch service)
    {
        _service = service;
        _panel.Children.Add(new TextBlock { Text = "搜索邮件", FontSize = 28, FontWeight = FontWeight.SemiBold });
        _field.ItemTemplate = new FuncDataTemplate<SearchField>((f, _) => new TextBlock { Text = f?.Name });
        _match.ItemTemplate = new FuncDataTemplate<SearchMatch>((m, _) => new TextBlock { Text = MatchName(m) });
        Field(_panel, "字段", _field, "search-field"); Field(_panel, "匹配方式", _match, "search-match");
        Field(_panel, "条件值", _value, "search-value"); _panel.Children.Add(_hint);
        Field(_panel, "结果排序", _order, "search-order");
        var toolbar = new WrapPanel(); Action(toolbar, "搜索", "search-run", RunInput);
        _back = Action(toolbar, "返回搜索结果", "search-back", async () =>
        {
            if (_previousRequest is not { } previous) return;
            _previousRequest = null; await Run(previous, _backId);
        });
        _back.IsVisible = false; _panel.Children.Add(toolbar);
        AutomationProperties.SetAutomationId(_coverage, "search-coverage"); _panel.Children.Add(_coverage);
        _results.ItemTemplate = new FuncDataTemplate<SearchHit>((m, _) => new TextBlock
        {
            Text = m is null ? "" : $"{m.Subject}\n{m.Path} · {m.From} · {m.Sent?.LocalDateTime.ToString("g") ?? "发件时间未知"} · {m.Size:N0} 字节",
            TextWrapping = TextWrapping.Wrap
        });
        Field(_panel, "结果", _results, "search-results");
        var nav = new WrapPanel();
        _prev = Action(nav, "上一页", "search-prev", () => Run(_request with { Offset = Math.Max(0, _request.Offset - _request.Limit) }));
        _next = Action(nav, "下一页", "search-next", () => Run(_request with { Offset = _request.Offset + _request.Limit }));
        _read = Action(nav, "打开邮件", "search-open", Read);
        _folder = Action(nav, "定位所在目录", "search-locate", async () =>
        {
            if (_results.SelectedItem is not SearchHit hit) return;
            _previousRequest = _request; _backId = hit.Id;
            await Locate(hit);
        });
        _panel.Children.Add(nav);
        AutomationProperties.SetAutomationId(_status, "search-status"); _panel.Children.Add(_status);
        AutomationProperties.SetAutomationId(_body, "search-body"); _panel.Children.Add(_body);
        Content = new ScrollViewer { Content = _panel };
        _field.SelectionChanged += (_, _) => SelectField();
        _field.ItemsSource = service.Fields; _field.SelectedIndex = 0;
        _results.SelectionChanged += (_, _) => { _body.Text = ""; SetSelection(); };
        _value.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Guard(RunInput); } };
        _prev.IsEnabled = _next.IsEnabled = false; SetSelection();
        AttachedToVisualTree += async (_, _) => await Guard(() => Run(new([])));
    }
    private static void Field(Panel panel, string label, Control control, string id)
    {
        panel.Children.Add(new TextBlock { Text = label }); AutomationProperties.SetName(control, label);
        AutomationProperties.SetAutomationId(control, id); panel.Children.Add(control);
    }
    private Button Action(Panel panel, string label, string id, Func<Task> work)
    {
        var button = new Button { Content = label, Margin = new Avalonia.Thickness(0, 0, 6, 2) }; AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await Guard(work); panel.Children.Add(button); return button;
    }
    private async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) _status.Text = e.Message; }
    }
    private static string MatchName(SearchMatch m) => m switch
    {
        SearchMatch.Contains => "包含",
        SearchMatch.Equals => "等于",
        SearchMatch.Before => "早于",
        SearchMatch.OnOrAfter => "当天或以后",
        SearchMatch.AtLeast => "不少于",
        _ => "不超过"
    };
    private void SelectField()
    {
        if (_field.SelectedItem is not SearchField field) return;
        _match.ItemsSource = field.Matches; _match.SelectedIndex = 0;
        _hint.Text = field.Kind switch
        {
            SearchKind.LocalDate => "本地日期 yyyy-MM-dd。当天从本地午夜起，至次日午夜前。",
            SearchKind.Number => "单位为字节。",
            SearchKind.Boolean => "输入「是」或「否」。",
            _ => ""
        };
    }
    private void SetSelection()
    {
        _read.IsEnabled = _folder.IsEnabled = _results.SelectedItem is SearchHit;
        _folder.IsEnabled &= _previousRequest is null;
        _read.Content = _results.SelectedItem is SearchHit { LocalId: null } ? "下载并打开" : "打开邮件";
    }
    private Task RunInput()
    {
        var criteria = _field.SelectedItem is SearchField field && _match.SelectedItem is SearchMatch match && !string.IsNullOrWhiteSpace(_value.Text)
            ? new[] { new FieldCriterion(field.Id, match, _value.Text!) } : [];
        _previousRequest = null;
        return Run(new(criteria, (SearchOrder)_order.SelectedIndex));
    }

    private async Task Locate(SearchHit hit)
    {
        var request = new SearchRequest([new("folderid", SearchMatch.Equals, hit.Folder.ToString())], _request.Order);
        var version = ++_version;
        var position = await _service.Position(hit, request.Order, _stop.Token);
        if (version == _version) await Run(request with { Offset = position / request.Limit * request.Limit }, hit.Id);
    }

    private async Task Run(SearchRequest request, string? selected = null)
    {
        var version = ++_version; _operation?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); _operation = operation;
        _status.Text = "正在搜索…";
        try
        {
            var page = await _service.Search(request, operation.Token);
            if (version == _version && !_stop.IsCancellationRequested) Show(request, page, selected);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        { if (version == _version && !_stop.IsCancellationRequested) _status.Text = e.Message; }
        finally { if (ReferenceEquals(_operation, operation)) _operation = null; }
    }
    private void Show(SearchRequest request, SearchPage result, string? selected)
    {
        _request = request; _results.ItemsSource = result.Items; _results.SelectedItem = result.Items.FirstOrDefault(m => m.Id == selected);
        _prev.IsEnabled = request.Offset > 0; _next.IsEnabled = result.HasMore; _back.IsVisible = _previousRequest is not null; SetSelection();
        _coverage.Text = $"当前工作空间 · 原文 {result.FullMessages} 封，仅摘要 {result.Summaries} 封，原文元数据不完整 {result.MissingMetadata} 封。";
        _status.Text = result.Items.Count == 0 ? "没有匹配邮件。" : $"第 {request.Offset / request.Limit + 1} 页 · {result.Items.Count} 封。";
    }
    private async Task Read()
    {
        if (_results.SelectedItem is not SearchHit hit) return;
        var version = _version;
        var body = await _service.Read(hit, _stop.Token);
        if (!_stop.IsCancellationRequested && version == _version && ReferenceEquals(hit, _results.SelectedItem))
        { _body.Text = body; _status.Text = $"{hit.Path} · {hit.Subject}"; }
    }
    public void Dispose() { _version++; _stop.Cancel(); _stop.Dispose(); }
}
