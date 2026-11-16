using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Desktop;

public sealed class ShellWindow : Window
{
    private readonly Contributions _pages;
    private readonly StackPanel _navigation = new() { Spacing = 8, Margin = new Thickness(16) };
    private readonly ContentControl _body = new() { Margin = new Thickness(32) };
    private string? _selected;
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), Foreground = Brush.Parse("#7C2D12") };

    public ShellWindow(Contributions pages)
    {
        _pages = pages;
        Title = "MyMailBox";
        Width = 1120; Height = 780; MinWidth = 860; MinHeight = 600;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 14;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        var sidebar = new Border { Background = Brush.Parse("#F1F5F9"), Child = _navigation };
        grid.Children.Add(sidebar);
        Grid.SetColumn(_body, 1); grid.Children.Add(_body);
        var layout = new DockPanel(); DockPanel.SetDock(_notice, Dock.Bottom); layout.Children.Add(_notice); layout.Children.Add(grid);
        AutomationProperties.SetAutomationId(_notice, "global-notice");
        Content = layout;
        pages.Changed += Refresh;
        pages.NoticeChanged += NoticeChanged;
        Closed += (_, _) => { pages.Changed -= Refresh; pages.NoticeChanged -= NoticeChanged; _body.Content = null; _navigation.Children.Clear(); };
        Refresh();
    }

    private void NoticeChanged() => Dispatcher.UIThread.Post(() => _notice.Text = _pages.Notice);

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
            button.Click += (_, _) => Show(id);
            _navigation.Children.Add(button);
        }
        Show(pages.Any(p => p.Id == _selected) ? _selected! : pages.FirstOrDefault()?.Id);
    }

    private void Show(string? id)
    {
        _selected = id;
        _body.Content = null;
        var page = _pages.Pages.FirstOrDefault(p => p.Id == id);
        if (page is null) return;
        var panel = new StackPanel { Spacing = 16, MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = page.Title, FontSize = 28, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = page.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#475569") });
        var readers = new Dictionary<string, Func<string>>();
        foreach (var field in page.Fields)
        {
            var fieldPanel = new StackPanel { Spacing = 6 };
            fieldPanel.Children.Add(new TextBlock { Text = field.Label, FontWeight = FontWeight.Medium });
            Control input;
            if (field.Choices is { } choices)
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
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false; result.Text = "正在处理…";
                try { result.Text = await page.Execute(readers.ToDictionary(p => p.Key, p => p.Value()), action.Id); }
                catch (Exception error) { result.Text = "操作未完成：" + error.Message; }
                finally { button.IsEnabled = true; }
            };
            actions.Children.Add(button);
        }
        panel.Children.Add(actions); panel.Children.Add(result);
        _body.Content = new ScrollViewer { Content = panel };
    }
}
