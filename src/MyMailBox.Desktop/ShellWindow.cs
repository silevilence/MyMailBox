using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyMailBox.Contracts;
using MyMailBox.Runtime;
using Page = MyMailBox.Contracts.Page;

namespace MyMailBox.Desktop;

public sealed class ShellWindow : Window
{
    private readonly Contributions _pages;
    private readonly StackPanel _navigation = new() { Spacing = 8, Margin = new Thickness(16) };
    private readonly ContentControl _body = new() { Margin = new Thickness(32) };
    private string? _selected;
    private NativePage? _nativePage;
    private object? _nativeView;
    private Page? _renderedPage;
    private IDisposable? _hostView;
    private int _selectorRevision;
    private int _exporterRevision;
    private readonly List<Action> _viewCleanup = [];
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), Foreground = Brush.Parse("#7C2D12") };
    private readonly Button _noticeClose = new() { Content = "关闭提示", IsVisible = false, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };

    public ShellWindow(Contributions pages)
    {
        _pages = pages;
        Title = "MyMailBox";
        Width = 1120; Height = 780; MinWidth = 760; MinHeight = 480;
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                Width = Math.Min(Width, screen.WorkingArea.Width / RenderScaling - 32);
                Height = Math.Min(Height, screen.WorkingArea.Height / RenderScaling - 32);
            }
        };
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 14;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        var sidebar = new Border { Background = Brush.Parse("#F1F5F9"), Child = new ScrollViewer { Content = _navigation } };
        grid.Children.Add(sidebar);
        Grid.SetColumn(_body, 1); grid.Children.Add(_body);
        var banner = new DockPanel(); DockPanel.SetDock(_noticeClose, Dock.Right); banner.Children.Add(_noticeClose); banner.Children.Add(_notice);
        _noticeClose.Click += (_, _) => _pages.Notice = ""; AutomationProperties.SetAutomationId(_noticeClose, "global-notice-close");
        var layout = new DockPanel(); DockPanel.SetDock(banner, Dock.Bottom); layout.Children.Add(banner); layout.Children.Add(grid);
        AutomationProperties.SetAutomationId(_notice, "global-notice");
        Content = layout;
        pages.Changed += Refresh;
        pages.NoticeChanged += NoticeChanged;
        Closed += (_, _) => { pages.Changed -= Refresh; pages.NoticeChanged -= NoticeChanged; ReleaseView(); _navigation.Children.Clear(); };
        Refresh();
    }

    private void NoticeChanged() => Dispatcher.UIThread.Post(() => { _notice.Text = _pages.Notice; _noticeClose.IsVisible = !string.IsNullOrWhiteSpace(_pages.Notice); });

    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh); return; }
        _navigation.Children.Clear();
        _navigation.Children.Add(new TextBlock { Text = "MyMailBox", FontSize = 23, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 24) });
        var pages = _pages.Pages;
        foreach (var page in pages)
        {
            var id = page.Id;
            var button = new Button { Content = page.Title, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, "nav-" + id);
            button.Click += async (_, _) => await Navigate(id);
            _navigation.Children.Add(button);
        }
        foreach (var page in _pages.NativePages)
        {
            var id = page.Id;
            var button = new Button { Content = page.Title, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, "nav-" + id);
            button.Click += async (_, _) => await Navigate(id); _navigation.Children.Add(button);
        }
        var selectedPage = pages.FirstOrDefault(p => p.Id == _selected);
        var selectedNative = _pages.NativePages.FirstOrDefault(p => p.Id == _selected);
        if (selectedPage is not null && ReferenceEquals(selectedPage, _renderedPage) &&
            (!selectedPage.Fields.Any(f => f.Selector is not null) || _selectorRevision == _pages.SelectorRevision) &&
            (selectedPage.Reader is null && selectedPage.Export is null || _exporterRevision == _pages.ExporterRevision)) return;
        if (selectedNative is not null && ReferenceEquals(selectedNative, _nativePage)) return;
        Show(selectedPage is not null || selectedNative is not null ? _selected! : pages.FirstOrDefault()?.Id);
    }

    private void ReleaseView()
    {
        foreach (var cleanup in _viewCleanup) cleanup();
        _viewCleanup.Clear();
        _body.Content = null;
        _hostView?.Dispose(); _hostView = null;
        if (_nativeView is not null) _nativePage!.Release(_nativeView);
        _nativeView = null; _nativePage = null;
        _renderedPage = null;
    }

    private async Task Navigate(string id)
    {
        if (_hostView is ComposerView draft && !await draft.SaveBeforeLeaving()) return;
        Show(id);
    }

    private ReplyIntent? _reply;
    private void Respond(ReplyIntent intent)
    {
        var composer=_pages.Pages.FirstOrDefault(p=>p.Composer is not null);
        if(composer is null){_pages.Notice="写信插件未启用。";return;}
        _reply=intent;Show(composer.Id);
    }
    private void Show(string? id)
    {
        _selected = id;
        ReleaseView();
        var native = _pages.NativePages.FirstOrDefault(p => p.Id == id);
        if (native is not null)
        {
            var view = native.Create();
            if (view is not Control control) { native.Release(view); throw new InvalidOperationException("高级页面未返回 Avalonia Control"); }
            _nativePage = native; _nativeView = view; _body.Content = control; return;
        }
        var page = _pages.Pages.FirstOrDefault(p => p.Id == id);
        if (page is null) return;
        _renderedPage = page; _selectorRevision = _pages.SelectorRevision; _exporterRevision = _pages.ExporterRevision;
        if (page.Composer is { } composer)
        {
            var view=new ComposerView(composer,_pages,page.Id.Split('/')[0],_reply);_reply=null;_hostView=view;_body.Content=view;return;
        }
        if(page.Automation is {} automation){var view=new AutomationView(automation);_hostView=view;_body.Content=view;return;}
        if (page.Rules is { } rules)
        {
            var view = new ReceiveRulesView(rules); _hostView = view; _body.Content = view; return;
        }
        if (page.Search is { } search)
        {
            var view = new MailSearchView(search,Respond); _hostView = view; _body.Content = view; return;
        }
        if (page.Organization is { } organization)
        {
            var view = new OrganizationView(organization); _hostView = view; _body.Content = view; return;
        }
        if (page.UnifiedInbox is { } inbox)
        {
            var view = new UnifiedInboxView(inbox,Respond); _hostView = view; _body.Content = view; return;
        }
        if (page.Protocols is { } protocols)
        {
            var view = new ProtocolView(protocols,Respond); _hostView = view; _body.Content = view; return;
        }
        if (page.Graph is { } graph)
        {
            var view = new GraphView(graph); _hostView = view; _body.Content = view; return;
        }
        if (page.Updates is { } updates)
        {
            var view = new UpdateView(updates, page.UpdateInstaller); _hostView = view; _body.Content = view; return;
        }
        if (page.Schedules is { } schedules)
        {
            var view = new ScheduleView(schedules); _hostView = view; _body.Content = view; return;
        }
        if (page.Reminders is { } reminders)
        {
            var view = new ReminderView(reminders); _hostView = view; _body.Content = view; return;
        }
        if (page.LocalMailboxes is { } local)
        {
            var view = new LocalMailboxView(local); _hostView = view; _body.Content = view; return;
        }
        if (page.Export is { } export)
        {
            var view = new ExportView(export, _pages.Exporters); _hostView = view; _body.Content = view; return;
        }
        if (page.Selections is { } selections)
        {
            var view = new SelectionView(selections); _hostView = view; _body.Content = view; return;
        }
        if (page.Accounts is { } accounts)
        {
            var view = new AccountsView(accounts); _hostView = view; _body.Content = view; return;
        }
        if (page.Reader is { } reader)
        {
            var view = new MailboxView(reader, _pages.Exporters,Respond); _hostView = view; _body.Content = view; return;
        }
        var panel = new StackPanel { Spacing = 16, MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = page.Title, FontSize = 28, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = page.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#475569") });
        var readers = new Dictionary<string, Func<string>>();
        foreach (var field in page.Fields)
        {
            var fieldPanel = new StackPanel { Spacing = 6 };
            fieldPanel.Children.Add(new TextBlock { Text = field.Label, FontWeight = FontWeight.Medium });
            Control input;
            if ((field.Selector is null ? field.Choices : _pages.Choices(field.Selector)) is { } choices)
            {
                var select = new ComboBox { ItemsSource = choices.Select(c => c.Label).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
                select.SelectedIndex = Math.Max(0, choices.ToList().FindIndex(c => c.Id == field.Value));
                readers[field.Id] = () => select.SelectedIndex < 0 ? "" : choices[select.SelectedIndex].Id;
                input = select;
            }
            else
            {
                var text = new TextBox { Text = field.Value, AcceptsReturn = field.Multiline, MinHeight = field.Multiline ? 100 : 36 };
                readers[field.Id] = () => text.Text ?? "";
                input = text;
                if (field.FilePicker)
                {
                    var browse = new Button { Content = "选择文件…" };
                    AutomationProperties.SetAutomationId(browse, "browse-" + field.Id);
                    browse.Click += async (_, _) =>
                    {
                        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = field.Label, AllowMultiple = false });
                        if (files.Count > 0) text.Text = files[0].Path.LocalPath;
                    };
                    fieldPanel.Children.Add(browse);
                }
            }
            AutomationProperties.SetName(input, field.Label);
            AutomationProperties.SetAutomationId(input, field.Id);
            fieldPanel.Children.Add(input); panel.Children.Add(fieldPanel);
        }
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#134E4A") };
        AutomationProperties.SetAutomationId(result, "action-result");
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var action in page.Actions)
        {
            var button = new Button { Content = action.Label, Margin = new Thickness(0, 0, 8, 8) };
            AutomationProperties.SetAutomationId(button, action.Id);
            EventHandler<Avalonia.Interactivity.RoutedEventArgs> handler = async (_, _) =>
            {
                button.IsEnabled = false; result.Text = "正在处理…";
                try { result.Text = await page.Execute(readers.ToDictionary(p => p.Key, p => p.Value()), action.Id); }
                catch (Exception error) { result.Text = "操作未完成：" + error.Message; }
                finally { button.IsEnabled = true; }
            };
            button.Click += handler;
            _viewCleanup.Add(() => button.Click -= handler);
            actions.Children.Add(button);
        }
        panel.Children.Add(actions); panel.Children.Add(result);
        _body.Content = new ScrollViewer { Content = panel };
    }
}
