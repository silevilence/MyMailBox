using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MyMailBox.Contracts;
using Page = MyMailBox.Contracts.Page;

namespace MyMailBox.Desktop;

internal sealed class PluginManagerView : UserControl, IDisposable
{
    private readonly Page _page;
    private readonly ListBox _list = new() { MinHeight = 130, MaxHeight = 310 };
    private readonly TextBox _search = new() { PlaceholderText = "查找插件" };
    private readonly CheckBox _showExamples=new(){Content="显示开发示例"};
    private readonly StackPanel _details = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _file = new() { PlaceholderText = "尚未选择插件包" };
    private readonly Dictionary<string, CheckBox> _checks = [];
    private bool _busy, _disposed;
    private readonly IDisposable? _watch;
    private readonly Func<IAccountService?> _accounts;
    private IReadOnlyList<MailAccount> _accountNames=[];
    private ManagedPlugin? Selected => _list.SelectedItem as ManagedPlugin;

    internal PluginManagerView(Page page, Func<IAccountService?> accounts)
    {
        _page = page; _accounts=accounts;
        _watch=page.WatchManagedPlugins?.Invoke(()=>Dispatcher.UIThread.Post(()=>{if(!_disposed&&!_busy)RefreshIfChanged();}));
        AttachedToVisualTree+=async(_,_)=>{
            try { if(_accounts() is {} service) { _accountNames=await service.List();if(!_disposed)Details(); } }
            catch(Exception e){if(!_disposed)_status.Text="账户列表暂时无法读取："+e.Message;}
        };
        var form = new StackPanel { Spacing = 10, MaxWidth = 900 };
        form.Children.Add(new TextBlock { Text = "插件管理", FontSize = 28, FontWeight = FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text = "选择插件即可更改启用状态。停用后仍会保留邮件和设置。", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(_search,"查找插件");
        AutomationProperties.SetAutomationId(_search,"plugin-search");
        _search.TextChanged += (_, _) => Reload();
        form.Children.Add(_search);
        var headings = new Grid { ColumnDefinitions = new("*,75,95"), ColumnSpacing = 8, Margin = new Thickness(12,0,16,0) };
        var captions = new[] { "插件名称", "版本", "状态" };
        for (var column = 0; column < captions.Length; column++)
        {
            var caption = new TextBlock { Text = captions[column], FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(caption,column); headings.Children.Add(caption);
        }
        form.Children.Add(headings);
        AutomationProperties.SetAutomationId(_list,"plugins-list"); AutomationProperties.SetName(_list,"已安装的插件");
        _list.ContainerPrepared += (_, e) =>
        {
            if (_list.Items[e.Index] is not ManagedPlugin plugin) return;
            AutomationProperties.SetAutomationId(e.Container, "plugin-row-"+plugin.Instance+"-"+plugin.Version);
            AutomationProperties.SetName(e.Container, $"{plugin.Name}，{plugin.Version}，{State(plugin)}");
        };
        _list.ItemTemplate = new FuncDataTemplate<ManagedPlugin>((p,_) =>
        {
            if (p is null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new("*,75,95"), ColumnSpacing = 8, Margin = new Thickness(4) };
            var name = new TextBlock { Text = p.Name, TextTrimming = TextTrimming.CharacterEllipsis };
            grid.Children.Add(name);
            var version = new TextBlock { Text = p.Version }; Grid.SetColumn(version,1); grid.Children.Add(version);
            var state = new TextBlock { Text = State(p) }; Grid.SetColumn(state,2); grid.Children.Add(state);
            return grid;
        });
        _list.SelectionChanged += (_,_) => Details();
        _showExamples.IsCheckedChanged+=(_,_)=>Reload();
        form.Children.Add(_list); form.Children.Add(_details);
        var install = new StackPanel { Spacing = 6 };
        install.Children.Add(new TextBlock { Text = "只安装来自可信来源的插件。插件可以访问本机文件。", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(_file,"package-path"); install.Children.Add(_file);
        var buttons = new WrapPanel(); install.Children.Add(buttons);
        Add(buttons,"选择插件包…","browse-package",async () =>
        {
            var top = TopLevel.GetTopLevel(this); if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title="选择插件包", FileTypeFilter=[new("ZIP 插件包"){Patterns=["*.zip"]}] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            _file.Text = path; _status.Text = await Execute("inspect-package");
        });
        Add(buttons,"查看插件信息","inspect-package",async()=>{ _status.Text=await Execute("inspect-package"); });
        Add(buttons,"安装所选插件包","install-package", async () =>
        {
            if (string.IsNullOrWhiteSpace(_file.Text)) { _status.Text="请先选择插件包。"; return; }
            _status.Text = await Execute("install-package"); Reload();
        });
        var installer=new Expander { Header = "安装插件", Content = install, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(installer,"plugin-install-tools");form.Children.Add(installer);
        var maintenance=new StackPanel{Spacing=4};maintenance.Children.Add(_showExamples);
        Add(maintenance,"重新读取插件设置","retry-config",async()=>{_status.Text=await Execute("retry-config");Reload();});
        form.Children.Add(new Expander{Header="高级选项",Content=maintenance});
        AutomationProperties.SetAutomationId(_status,"action-result"); Content = PageFeedback.Layout(form,_status);
        Reload();
    }
    private static string State(ManagedPlugin p) => p.NeedsRestart ? "已停用，待重启" : p.Core ? "系统组件" : p.Enabled ? "已启用" : p.CurrentVersion != null && p.CurrentVersion != p.Version ? "其他版本" : "未启用";
    private void RefreshIfChanged()
    {
        try
        {
            var current=VisiblePlugins();
            static string Key(ManagedPlugin p)=>string.Join("|",p.Instance,p.Version,p.CurrentVersion,p.Enabled,p.NeedsRestart,string.Join(",",p.Grants));
            if(!current.Select(Key).SequenceEqual(_list.Items.OfType<ManagedPlugin>().Select(Key)))Reload();
        }
        catch(Exception e){_status.Text="插件设置暂时无法读取："+e.Message;}
    }
    public void Dispose(){_disposed=true;_watch?.Dispose();}
    private ManagedPlugin[] VisiblePlugins() => _page.ManagedPlugins!()
        .Where(p=>_showExamples.IsChecked==true || !p.Builtin || p.Plugin is not ("prototype" or "selector"))
        .Where(p=>p.Name.Contains(_search.Text?.Trim() ?? "", StringComparison.CurrentCultureIgnoreCase))
        .ToArray();
    private void Reload()
    {
        var old = Selected;
        var items = VisiblePlugins();
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(p => p.Instance == old?.Instance && p.Version == old.Version) ?? items.FirstOrDefault();
        Details();
    }
    private void Details()
    {
        _details.Children.Clear(); _checks.Clear();
        var permissions = new StackPanel { Spacing = 3 };
        if (Selected is not { } item) return;
        _details.Children.Add(new TextBlock { Text = item.Name + "  ·  " + (item.Builtin ? "随软件安装" : "外部插件"), FontWeight = FontWeight.SemiBold });
        if (item.NeedsRestart) _details.Children.Add(new TextBlock { Text = "插件已停止工作。重新打开软件后，可再次启用或更换这个插件。", TextWrapping=TextWrapping.Wrap });
        if (item.Dependencies.Length > 0)
        {
            var names = _page.ManagedPlugins!().ToLookup(p=>p.Plugin,p=>p.Name);
            _details.Children.Add(new TextBlock { Text="需要同时启用："+string.Join("、",item.Dependencies.Select(d=>names[d].FirstOrDefault()??d)), TextWrapping=TextWrapping.Wrap });
        }
        var scoped=item.Permissions.Where(p=>p is "mail.read" or "mail.write" or "mail.send" or "contacts.suggest")
            .SelectMany(p=>_accountNames.Select(a=>p+":"+a.Id));
        foreach (var grant in item.Permissions.Concat(scoped).Concat(item.Grants).Distinct())
        {
            var parts=grant.Split(':',2);
            var title=MyMailBox.Builtins.PluginPermissionName.Display(parts[0]);
            if(parts.Length==2)title+=" · "+(_accountNames.FirstOrDefault(a=>a.Id.ToString()==parts[1])?.Name??"原账户");
            else if(scoped.Any(id=>id.StartsWith(grant+":",StringComparison.Ordinal)))title+=" · 全部账户";
            var check = new CheckBox { Content = title, IsChecked = item.Grants.Contains(grant) };
            _checks.Add(grant,check); permissions.Children.Add(check);
        }
        if (_checks.Count > 0) _details.Children.Add(new Expander { Header="允许插件访问的内容", Content=permissions, IsExpanded=true });
        var tools = new WrapPanel(); _details.Children.Add(tools);
        if (item.Core)
        {
            _details.Children.Add(new TextBlock { Text = "系统组件在启动时加载。更换版本后，重新打开软件即可生效。" });
            if (item.Configured && item.CurrentVersion != item.Version) Add(tools,"使用此版本","upgrade-plugin",()=>Change("upgrade-plugin"));
        }
        else if (item.CurrentVersion != null && item.CurrentVersion != item.Version)
            Add(tools,"切换到此版本","upgrade-plugin",()=>Change("upgrade-plugin"));
        else
        {
            Add(tools,item.Enabled ? "停用" : "启用",item.Enabled ? "disable-plugin" : "enable-plugin",()=>Toggle(item));
            if (item.Enabled && _checks.Count > 0) Add(tools,"保存权限","save-grants",()=>Change("enable-plugin"));
        }
        if (!item.Builtin)
        {
            if (!item.Core && item.Configured && item.CurrentVersion == item.Version) Add(tools,"移除配置…","remove-instance",()=>Confirm("移除这个插件的设置？","remove-instance"));
            if (item.CurrentVersion != item.Version) Add(tools,"卸载此版本…","remove-package",()=>Confirm("卸载选中的插件版本？","remove-package"));
        }
    }
    private Task Toggle(ManagedPlugin item)
    {
        var active=_page.ManagedPlugins!().Where(p=>p.Enabled).ToArray();
        if(!item.Enabled)
        {
            var missing=item.Dependencies.Where(id=>!active.Any(p=>p.Plugin==id)).ToArray();
            if(missing.Length>0)
            {
                var all=_page.ManagedPlugins!();
                _status.Text="请先启用以下插件："+string.Join("、",missing.Select(id=>all.FirstOrDefault(p=>p.Plugin==id)?.Name??id));
                return Task.CompletedTask;
            }
            return Change("enable-plugin");
        }
        var affected=new HashSet<string>{item.Instance};
        int before;
        do
        {
            before=affected.Count;
            var unavailable=active.Where(p=>affected.Contains(p.Instance)).Select(p=>p.Plugin).Except(active.Where(p=>!affected.Contains(p.Instance)).Select(p=>p.Plugin)).ToHashSet();
            foreach(var p in active.Where(p=>p.Dependencies.Any(unavailable.Contains)))affected.Add(p.Instance);
        }while(before!=affected.Count);
        var names=active.Where(p=>p.Instance!=item.Instance && affected.Contains(p.Instance)).Select(p=>p.Name).ToArray();
        return names.Length==0 ? Change("disable-plugin") : Confirm("以下插件也会停用："+string.Join("、",names)+"。继续吗？","disable-plugin");
    }
    private Task Confirm(string text,string action)
    {
        Details(); var row = new WrapPanel(); _details.Children.Add(new TextBlock { Text=text }); _details.Children.Add(row);
        Add(row,"确认","confirm-plugin-action",()=>Change(action));
        Add(row,"取消","cancel-plugin-action",()=>{Details();return Task.CompletedTask;});
        return Task.CompletedTask;
    }
    private async Task Change(string action)
    {
        _status.Text = await Execute(action); Reload();
    }
    private Task<string> Execute(string action)
    {
        var p = Selected;
        return _page.Execute(new Dictionary<string,string> { ["package-path"]=_file.Text??"", ["plugin-id"]=p?.Plugin??"",
            ["managed-instance"]=p?.Instance??"", ["plugin-version"]=p?.Version??"", ["fail-initialization"]="no",
            ["grants"]=string.Join(",",_checks.Where(c=>c.Value.IsChecked==true).Select(c=>c.Key)) },action);
    }
    private void Add(Panel panel,string label,string id,Func<Task> action)
    {
        var button = new Button { Content=label, Margin=new Thickness(0,2,8,2) };
        AutomationProperties.SetAutomationId(button,id);
        button.Click += async (_,_) =>
        {
            if (_busy) return;
            _busy = true; IsEnabled = false;
            try { await action(); }
            catch (Exception e) { _status.Text="操作未完成："+e.Message; }
            finally { _busy=false; IsEnabled=true; }
        };
        panel.Children.Add(button);
    }
}
