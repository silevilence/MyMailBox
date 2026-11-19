using Avalonia;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class UnifiedInboxView : UserControl, IDisposable
{
    private sealed record Filter(Guid? Id, string Name) { public override string ToString() => Name; }
    private readonly IUnifiedInbox _service;
    private readonly CancellationTokenSource _stop = new();
    private readonly ComboBox _filter = new();
    private readonly TextBlock _rangeLabel = new() { Text = "每次收取封数", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _subject = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sender = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { Text = "选择一封邮件，阅读正文或回复。", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _onboard = new() { Spacing = 8 };
    private bool _bindingPage;
    private readonly ComboBox _range = new() { ItemsSource = new[] { 50, 200, 500 }, SelectedIndex = 0 };
    private readonly TextBlock _states = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _body = new() { IsReadOnly = true, AcceptsReturn = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new() { MinHeight = 86 };
    private readonly Button _sync, _toggle, _prev, _next, _open;
    private readonly Stack<InboxPosition?> _back = new();
    private InboxPosition? _before;
    private IReadOnlyList<ReceiveStatus> _rows = [];
    private UnifiedPage _page = new([], false);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _settingFilter, _checking;
    private int _revision, _reading;
    private Guid? Account => (_filter.SelectedItem as Filter)?.Id;

    public UnifiedInboxView(IUnifiedInbox service,Action<ReplyIntent>? respond=null,Guid? linked=null, Func<string,Task>? navigate=null, bool canAdd=false, Action<string>? search=null)
    {
        _service = service;
        var layout = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing=8 };
        var heading = new DockPanel();
        if(search!=null)
        {
            var searchBox=new TextBox{PlaceholderText="搜索邮件主题",MaxWidth=260,MinWidth=140,VerticalAlignment=VerticalAlignment.Center};
            AutomationProperties.SetName(searchBox,"搜索邮件主题");AutomationProperties.SetAutomationId(searchBox,"inbox-search");
            void FindMail() { if (!string.IsNullOrWhiteSpace(searchBox.Text)) search(searchBox.Text.Trim()); }
            searchBox.KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Enter){e.Handled=true;FindMail();}};
            var startSearch = new Button { Content = "搜索", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(startSearch,"inbox-search-go");
            startSearch.Click += (_,_) => FindMail();
            var searchTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            searchTools.Children.Add(searchBox); searchTools.Children.Add(startSearch);
            DockPanel.SetDock(searchTools,Dock.Right);heading.Children.Add(searchTools);
        }
        heading.Children.Add(new TextBlock { Text = "收件箱", FontSize = 28, FontWeight = FontWeight.SemiBold });
        layout.Children.Add(heading);
        var toolbar = new StackPanel { Spacing=5 };
        var actions = new WrapPanel(); toolbar.Children.Add(actions);
        static void Identify(Control control,string id,string name)
        { AutomationProperties.SetAutomationId(control,id); AutomationProperties.SetName(control,name); }
        Identify(_filter,"unified-filter","查看账户"); _filter.MinWidth=120; actions.Children.Add(_filter);
        Button Action(Panel holder,string title,string id,Func<Task> action)
        {
            var button = new Button { Content=title, Margin=new Thickness(4,0,0,4) };
            Identify(button,id,title); button.Click+=async (_,_)=>await Guard(action); holder.Children.Add(button); return button;
        }
        _sync = Action(actions,"收取邮件","unified-sync",Refresh);
        _toggle = Action(actions,"开启自动接收","unified-auto",Configure);
        actions.Children.Add(_rangeLabel); Identify(_range,"unified-range","每次收取封数"); actions.Children.Add(_range);
        Identify(_states,"unified-status","收信状态"); var stateScroll = new ScrollViewer { Content=_states,MaxHeight=80 };
        var statesPanel=new CheckBox{Content="显示收信状态",Margin=new Thickness(12,0,0,0)};
        AutomationProperties.SetAutomationId(statesPanel,"inbox-sync-details");actions.Children.Add(statesPanel);stateScroll.IsVisible=false;
        statesPanel.IsCheckedChanged+=(_,_)=>stateScroll.IsVisible=statesPanel.IsChecked==true;toolbar.Children.Add(stateScroll);
        if (canAdd && navigate != null)
        {
            _onboard.Children.Add(new TextBlock { Text="添加邮箱账户后，就可以在这里收信。" });
            Action(_onboard,"添加邮箱账户","inbox-add-account",()=>navigate("accounts/manage"));
        }
        _onboard.IsVisible=false; toolbar.Children.Add(_onboard);
        Grid.SetRow(toolbar,1); layout.Children.Add(toolbar);
        var columns=new Grid { ColumnDefinitions=new("2*,3*"), RowDefinitions=new("*"), ColumnSpacing=12 };
        Grid.SetRow(columns,2); layout.Children.Add(columns);
        var messages=new Grid { RowDefinitions=new("*,Auto"),RowSpacing=4 };
        Identify(_list,"unified-list","邮件列表");
        _list.ItemTemplate=new FuncDataTemplate<InboxMessage>((m,_)=>{
            var entry=new StackPanel { Spacing=3, Margin=new Thickness(2,4) };
            if(m is null)return entry;
            var line=new DockPanel();
            var date=DateTimeOffset.FromUnixTimeMilliseconds(m.Position.UnixMilliseconds).LocalDateTime;
            var time=new TextBlock { Text=date.ToString(date.Date==DateTime.Today?"HH:mm":"M/d"), FontSize=12 };
            DockPanel.SetDock(time,Dock.Right); line.Children.Add(time);
            line.Children.Add(new TextBlock{Text=MimeKit.MailboxAddress.TryParse(m.Sender,out var sender) && !string.IsNullOrWhiteSpace(sender.Name)?sender.Name:m.Sender,FontWeight=FontWeight.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis});
            entry.Children.Add(line);
            entry.Children.Add(new TextBlock{Text=string.IsNullOrWhiteSpace(m.Subject)?"（无主题）":m.Subject,TextTrimming=TextTrimming.CharacterEllipsis});
            entry.Children.Add(new TextBlock{Text=m.AccountName,FontSize=12,Foreground=Brush.Parse("#4A6074")});
            return entry;
        });
        messages.Children.Add(_list); var paging=new WrapPanel(); Grid.SetRow(paging,1); messages.Children.Add(paging);
        _prev=Action(paging,"上一页","unified-prev",async()=>{_before=_back.Pop();await Page();});
        _next=Action(paging,"下一页","unified-next",async()=>{if(!_page.HasMore)return;_back.Push(_before);_before=_page.Messages[^1].Position;await Page();});
        columns.Children.Add(messages);
        var reader = new Grid { RowDefinitions=new("Auto,Auto,Auto,Auto,*"), RowSpacing=6 };
        var surface=new Border { Padding=new Thickness(12),BorderBrush=Brush.Parse("#CCD7E0"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Child=reader };
        Grid.SetColumn(surface,1);columns.Children.Add(surface);
        reader.Children.Add(_empty);
        var backToList = new Button { Content = "返回邮件列表", IsVisible = false };
        Identify(backToList, "unified-back", "返回邮件列表");
        reader.Children.Add(backToList);
        backToList.Click += (_, _) => { _list.SelectedItem = null; _list.Focus(); };
        Grid.SetRow(_subject,1);reader.Children.Add(_subject);
        Grid.SetRow(_sender,2); reader.Children.Add(_sender);
        var reply=new WrapPanel();Grid.SetRow(reply,3);reader.Children.Add(reply);
        _open=Action(reply,"阅读","unified-open",Open);
        Action(reply,"查看原邮件及附件","unified-original",async()=>{
            if(_list.SelectedItem is not InboxMessage mail || TopLevel.GetTopLevel(this) is not Window owner)return;
            if(!mail.Cached) await _service.Open(mail.Account,mail.Position.Key,_stop.Token);
            var raw=await _service.CachedOriginal(mail.Account,mail.Position.Key,_stop.Token);
            if (_stop.IsCancellationRequested || _list.SelectedItem is not InboxMessage current || current.Position!=mail.Position) return;
            if(raw is null){_feedback.Text="这个邮箱插件只提供正文。";return;}
            await OriginalMailWindow.Show(owner,raw,_stop.Token);
        });
        ReplyButtons.Add(reply,"unified",()=> (_list.SelectedItem as InboxMessage)?.Position.Key,respond,s=>_feedback.Text=s);
        Identify(_body,"unified-body","邮件正文"); Grid.SetRow(_body,4);reader.Children.Add(_body);
        Identify(_feedback,"unified-feedback","邮件状态");AutomationProperties.SetLiveSetting(_feedback,AutomationLiveSetting.Polite);
        Grid.SetRow(_feedback,3);layout.Children.Add(_feedback); Content=layout;
        void ArrangePanes(double width)
        {
            bool compact = width < 640;
            bool reading = compact && _list.SelectedItem is InboxMessage;
            if (columns.ColumnDefinitions.Count != (compact ? 1 : 2))
                columns.ColumnDefinitions = new(compact ? "*" : "2*,3*");
            Grid.SetColumn(surface, compact ? 0 : 1);
            heading.IsVisible = toolbar.IsVisible = !reading;
            messages.IsVisible = !reading;
            surface.IsVisible = !compact || reading;
            backToList.IsVisible = reading;
        }
        SizeChanged+=(_,e)=>ArrangePanes(e.NewSize.Width);
        _filter.SelectionChanged += async (_, _) =>
        {
            if (_settingFilter) return;
            _back.Clear(); _before = null; DisplayStates(true); await Guard(Page);
        };
        _range.SelectionChanged += async (_, _) =>
        {
            if (_range.SelectedItem is int range && _rows.FirstOrDefault(r => r.Account == Account) is { Available: true } row && row.Range != range)
                await Guard(async () => { await _service.Configure(row.Account, row.Automatic, range, _stop.Token); await Update(false); });
        };
        _list.SelectionChanged += async (_, _) =>
        {
            _reading++; _body.Text = "";
            var item = _list.SelectedItem as InboxMessage;
            _subject.Text=item?.Subject; _sender.Text=item?.Sender; _empty.IsVisible=item is null; reply.IsVisible=item is not null;
            ArrangePanes(Bounds.Width);
            Buttons();
            if (!_bindingPage && item?.Cached == true) await Guard(Open);
        };
        reply.IsVisible=false;
        _timer.Tick += async (_, _) => await Guard(() => Update(false));
        AttachedToVisualTree += async (_, _) => { await Guard(async()=>{await Update(true);if(linked.HasValue)_filter.SelectedItem=_filter.Items.OfType<Filter>().FirstOrDefault(f=>f.Id==linked);}); if (!_stop.IsCancellationRequested) _timer.Start(); };
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) _feedback.Text = e.Message; }
    }

    private async Task Update(bool force)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var rows = await _service.Status(_stop.Token);
            if (_stop.IsCancellationRequested) return;
            var filters = rows.Select(r => new Filter(r.Account, r.Name)).Prepend(new(null, "全部账户")).ToArray();
            bool filterChanged = _filter.ItemsSource is not Filter[] current || !current.SequenceEqual(filters);
            var changedInbox = !_rows.Select(r => (r.Account, r.LastSuccess)).SequenceEqual(rows.Select(r => (r.Account, r.LastSuccess)));
            _rows = rows;
            if (filterChanged)
            {
                var id = Account; _settingFilter = true;
                try { _filter.ItemsSource = filters; _filter.SelectedItem = filters.FirstOrDefault(f => f.Id == id) ?? filters[0]; }
                finally { _settingFilter = false; }
            }
            DisplayStates(filterChanged);
            if (force || filterChanged || (changedInbox && _before is null)) await Page();
        }
        finally { _checking = false; }
    }

    private void DisplayStates(bool resetRange)
    {
        var selected = _rows.FirstOrDefault(r => r.Account == Account);
        var visible = selected is null ? _rows : [selected];
        _states.Text = visible.Count == 0 ? "请先在账户管理添加收信账户。" : string.Join("\n", visible.Select(r =>
        {
            var state = r.State switch
            {
                "running" => "接收中", "queued" => "已排队", "offline" => "离线", "auth" => "需要重新授权",
                "certificate" => "TLS 连接失败", "permission" => "需要插件授权", "limited" => "服务限流", "policy" => "需要管理员确认", "storage" => "存储空间不足",
                "ready" => "接收完成", "failed" => "接收失败", "unavailable" => "不可接收", _ => "已暂停"
            };
            return $"{r.Name} · {state} · 最近成功 {r.LastSuccess?.LocalDateTime.ToString("g") ?? "无"}　{r.Detail}";
        }));
        _toggle.IsVisible = _range.IsVisible = selected?.Available == true;
        _onboard.IsVisible = _rows.Count == 0 && _onboard.Children.Count > 0;
        _rangeLabel.IsVisible = _range.IsVisible;
        _toggle.Content = selected?.Automatic == true ? "暂停自动接收" : "开启自动接收";
        if (resetRange && selected is not null) _range.SelectedItem = selected.Range;
        _sync.IsEnabled = visible.Any(r => r.Available);
    }

    private async Task Refresh()
    {
        foreach (var row in _rows.Where(r => r.Available && (Account is null || r.Account == Account)))
            await _service.Refresh(row.Account, _stop.Token);
        _feedback.Text = "正在收取邮件。"; await Update(false);
    }

    private async Task Configure()
    {
        var selected = _rows.Single(r => r.Account == Account);
        await _service.Configure(selected.Account, !selected.Automatic, _range.SelectedItem as int? ?? 50, _stop.Token);
        await Update(false);
    }

    private async Task Page()
    {
        var revision = ++_revision;
        var page = await _service.Inbox(Account, _before, 25, _stop.Token);
        if (_stop.IsCancellationRequested || revision != _revision) return;
        var selected = _list.SelectedItem as InboxMessage;
        var text = _body.Text;
        _page = page; _bindingPage = true; _list.ItemsSource = page.Messages;
        _list.SelectedItem = page.Messages.FirstOrDefault(m => m.Account == selected?.Account && m.Position == selected.Position);
        _bindingPage = false;
        if (_list.SelectedItem is not null) _body.Text = text;
        _feedback.Text = page.Messages.Count == 0 ? "这里还没有邮件。" : $"第 {_back.Count + 1} 页 · {page.Messages.Count} 封";
        Buttons();
    }

    private void Buttons()
    {
        _prev.IsEnabled = _back.Count > 0; _next.IsEnabled = _page.HasMore; _open.IsEnabled = _list.SelectedItem is InboxMessage;
        _open.IsVisible = _list.SelectedItem is InboxMessage{Cached:false};
        _open.Content = _list.SelectedItem is InboxMessage { Cached: false } ? "下载并阅读" : "离线阅读";
    }

    private async Task Open()
    {
        if (_list.SelectedItem is not InboxMessage item) return;
        var revision = ++_reading;
        _feedback.Text = item.Cached ? "正在读取本地副本…" : "正在下载邮件…";
        var text = await _service.Open(item.Account, item.Position.Key, _stop.Token);
        if (_stop.IsCancellationRequested || revision != _reading) return;
        _body.Text = text; _feedback.Text = "";
    }

    public void Dispose() { _timer.Stop(); _stop.Cancel(); _stop.Dispose(); }
}
