using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed partial class ShellWindow
{
    private readonly HashSet<string> _expandedGroups = [];
    private readonly Dictionary<string, Button> _links = [];
    private bool _userNavigated;
    private bool _startupComplete;
    private static readonly IBrush Ink = Brush.Parse("#233751"), Accent = Brush.Parse("#235785");
    private sealed record Destination(string Id, string Name, string Group, string Icon);
    private static readonly (string Id, string Name, string Group, string Icon)[] Menu =
    [
        ("accounts/compose","写邮件","邮件","\uE70F"), ("accounts/inbox","收件箱","邮件","\uE715"),
        ("accounts/mailbox-browser","邮箱文件夹","邮件","\uE8B7"), ("accounts/search","搜索邮件","邮件","\uE721"),
        ("accounts/drafts","草稿箱","邮件","\uE8B7"), ("local-mail/sent","已发送","邮件","\uE73E"), ("accounts/outbox","发件箱","邮件","\uE724"),
        ("local-mail/inbox","本地邮件","邮件","\uE8B7"), ("accounts/organize","整理邮件","邮件工具","\uE8B7"),
        ("accounts/rules","收件规则","邮件工具","\uE8FD"), ("accounts/automation","自动任务","邮件工具","\uE768"),
        ("reminders/list","提醒","邮件工具","\uE91C"), ("reminders/recurring","重复计划","邮件工具","\uE787"),
        ("reminders/activity","通知记录","邮件工具","\uE81C"), ("mail-selectors/choose","常用账户和文件夹","邮件工具","\uE734"),
        ("accounts/manage","邮箱账户","设置","\uE77B"), ("plugin-manager","插件管理","设置","\uE74C"),
        ("accounts/protocols","收发设置","设置","\uE774"), ("accounts/graph","Outlook.com 邮件","设置","\uE715"),
        ("accounts/exchange","Exchange 连接","设置","\uE774"), ("updates/settings","软件更新","设置","\uE895"),
        ("desktop/lifetime","启动和退出","设置","\uE713"), ("diagnostics","权限和诊断","设置","\uE9D9"),
        ("local-mail/local-mailboxes","本地邮箱管理","导入与备份","\uE8B7"), ("local-mail/pst-import","导入邮件文件","导入与备份","\uE8B5"),
        ("accounts/outlook-import","导入 Outlook 账户","导入与备份","\uE77B"), ("local-mail/export","导出邮件","导入与备份","\uE8A5"),
        ("local-mail/archive","邮件归档","导入与备份","\uE7B8"), ("local-mail/backup","备份和恢复","导入与备份","\uE896"),
        ("local-mail/storage","邮件存储","导入与备份","\uE977"), ("accounts/templates","导入导出设置","导入与备份","\uE713"),
        ("welcome/welcome","使用帮助","设置","\uE897")
    ];

    private Destination[] Destinations()
    {
        var available = _pages.Pages.Select(p => (p.Id, p.Title)).Concat(_pages.NativePages.Select(p => (p.Id, p.Title))).ToDictionary(p => p.Id, p => p.Title);
        var result = new List<Destination>();
        foreach (var item in Menu)
            if (available.Remove(item.Id)) result.Add(new(item.Id, item.Name, item.Group, item.Icon));
        foreach (var (id, name) in available)
            if (id != "plugin-settings" && !_pages.Pages.Any(p => p.Id == id && p.DevelopmentOnly))
                result.Add(new(id, name, "其他功能", "\uE8A5"));
        return result.ToArray();
    }

    private void BuildNavigation(Destination[] destinations)
    {
        _links.Clear();
        _navigation.Children.Clear();
        _navigation.Children.Add(new TextBlock { Text = "MyMailBox", FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = Ink, Margin = new Thickness(10,8,0,16) });
        foreach (var group in destinations.GroupBy(d => d.Group))
        {
            var items = new StackPanel { Spacing = 3 };
            if (group.Key == "邮件") _navigation.Children.Add(items);
            else
            {
                var section = new Expander { Header = group.Key, Content = items, IsExpanded = _expandedGroups.Contains(group.Key), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4) };
                AutomationProperties.SetAutomationId(section,"nav-group-"+group.Key);
                section.Expanded += (_,_) => _expandedGroups.Add(group.Key);
                section.Collapsed += (_,_) => _expandedGroups.Remove(group.Key);
                _navigation.Children.Add(section);
            }
            foreach (var d in group)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(new TextBlock { Text = d.Icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = d.Name, VerticalAlignment = VerticalAlignment.Center });
                var button = new Button { Content = row, MinHeight = 36, CornerRadius = new CornerRadius(4), Padding = new Thickness(10,5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(button, d.Name); AutomationProperties.SetAutomationId(button, "nav-"+d.Id);
                button.Click += async (_,_) => await Navigate(d.Id);
                items.Children.Add(button); _links.Add(d.Id,button);
            }
        }
        Highlight();
    }

    private void Highlight()
    {
        foreach (var (id, button) in _links)
        {
            bool compose = id == "accounts/compose", selected = id == _selected;
            button.Background = compose ? Accent : selected ? Brush.Parse("#DBE9F5") : Brushes.Transparent;
            button.Foreground = compose ? Brushes.White : Ink;
            button.FontWeight = selected || compose ? FontWeight.SemiBold : FontWeight.Normal;
            button.BorderThickness = new Thickness(selected ? 3 : 0,0,0,0);
            button.BorderBrush = Accent;
        }
    }

    internal void FinishStartup()
    {
        _startupComplete = true;
        if (!_userNavigated || _selected is null)
            Show(Destinations().FirstOrDefault(d => d.Id == "accounts/inbox")?.Id ?? DefaultDestination());
    }

    private string? DefaultDestination() => Destinations().OrderBy(d => d.Id switch { "recovery"=>0, "accounts/inbox"=>1, "local-mail/inbox"=>2, "plugin-manager"=>3, _=>4 }).FirstOrDefault()?.Id;
}
