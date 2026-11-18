using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class ActivityView:UserControl,IDisposable
{
    private readonly ActivityPage _page;private readonly CancellationTokenSource _stop=new();
    private readonly ListBox _entries=new(){MaxHeight=280};private readonly ComboBox _account=new(),_kind=new(),_presenter=new();
    private readonly CheckBox _quiet=new(){Content="免打扰，仅记入活动中心"},_private=new(){Content="锁屏时隐藏邮件和提醒内容"},_enabled=new(){Content="显示此账户及类型的通知"};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Dictionary<string,string> _categories=new(){["received"]="新邮件",["sync"]="同步完成",["error"]="失败",["reminder"]="提醒",["task"]="自动化"};
    private int _offset;
    public ActivityView(ActivityPage page,IReadOnlyList<NotificationPresenter> presenters,Action<ActivityLink> open)
    {
        _page=page;var p=new StackPanel{Spacing=7,MaxWidth=840};p.Children.Add(new TextBlock{Text="活动中心",FontSize=28,FontWeight=FontWeight.SemiBold});
        _entries.ItemTemplate=new FuncDataTemplate<ActivityEntry>((e,_)=>new TextBlock{Text=e is null?"":$"{e.At.LocalDateTime:g} · {e.Title}\n{e.Detail}",TextWrapping=TextWrapping.Wrap});
        Field(p,"最近活动",_entries,"activity-items");var actions=new WrapPanel();
        Button(actions,"刷新","activity-refresh",Load);Button(actions,"上一页","activity-prev",async()=>{_offset=Math.Max(0,_offset-25);await Load();});
        Button(actions,"下一页","activity-next",async()=>{if(_entries.ItemCount==25){_offset+=25;await Load();}});
        Button(actions,"打开关联内容","activity-open",async()=>{if(_entries.SelectedItem is ActivityEntry row)open(await page.Center.Resolve(row.Id,_stop.Token));});p.Children.Add(actions);
        var settings=new StackPanel{Spacing=4};settings.Children.Add(_quiet);settings.Children.Add(_private);_presenter.ItemsSource=new[]{"builtin"}.Concat(presenters.Select(x=>x.Id)).ToArray();
        Field(settings,"通知呈现插件",_presenter,"activity-presenter");Button(settings,"保存通知设置","activity-save",async()=>
        {await page.Center.Configure(new(_quiet.IsChecked==true,_private.IsChecked==true,_presenter.SelectedItem as string??"builtin"),_stop.Token);_status.Text="已保存通知设置。";});
        _account.ItemTemplate=new FuncDataTemplate<Choice>((s,_)=>new TextBlock{Text=s?.Label});Field(settings,"账户",_account,"activity-account");
        _kind.ItemsSource=_categories.Values.ToArray();_kind.SelectedIndex=0;Field(settings,"事件类型",_kind,"activity-kind");settings.Children.Add(_enabled);
        Button(settings,"保存类型设置","activity-filter-save",async()=>{var (k,a)=Filter();await page.Center.SetEnabled(k,a,_enabled.IsChecked==true,_stop.Token);_status.Text="已保存此范围的通知设置。";});
        p.Children.Add(new Expander{Header="通知设置",Content=settings});AutomationProperties.SetAutomationId(_status,"activity-status");p.Children.Add(_status);Content=PageFeedback.Layout(p,_status);
        _kind.SelectionChanged+=async(_,_)=>await Guard(ReadFilter);_account.SelectionChanged+=async(_,_)=>await Guard(ReadFilter);
        AttachedToVisualTree+=async(_,_)=>await Guard(async()=>
        {
            var policy=await page.Center.Policy(_stop.Token);_quiet.IsChecked=policy.Quiet;_private.IsChecked=policy.HideWhileLocked;_presenter.SelectedItem=policy.Presenter;
            _account.ItemsSource=new[]{new Choice("","全部账户")}.Concat((await page.Accounts.List(_stop.Token)).Select(a=>new Choice(a.Id.ToString(),a.Name))).ToArray();
            _account.SelectedIndex=0;await Load();
        });
    }
    private (string,Guid?) Filter()=> (_categories.Keys.ElementAt(Math.Max(0,_kind.SelectedIndex)),Guid.TryParse((_account.SelectedItem as Choice)?.Id,out var id)?id:null);
    private async Task ReadFilter(){var (kind,account)=Filter();var enabled=await _page.Center.Enabled(kind,account,_stop.Token);if(Filter()==(kind,account))_enabled.IsChecked=enabled;}
    private async Task Load()
    {
        var rows=await _page.Center.Recent(_offset,25,_stop.Token);_entries.ItemsSource=rows;_entries.SelectedIndex=rows.Count>0?0:-1;
        _status.Text=rows.Count==0?"暂无活动。":$"第 {_offset+1}–{_offset+rows.Count} 项。";
    }
    private static void Field(Panel p,string label,Control control,string id){p.Children.Add(new TextBlock{Text=label});AutomationProperties.SetName(control,label);AutomationProperties.SetAutomationId(control,id);p.Children.Add(control);}
    private void Button(Panel p,string label,string id,Func<Task> action){var b=new Button{Content=label};AutomationProperties.SetAutomationId(b,id);b.Click+=async(_,_)=>await Guard(action);p.Children.Add(b);}
    private async Task Guard(Func<Task> work){try{await work();}catch(OperationCanceledException){}catch(Exception e){if(!_stop.IsCancellationRequested)_status.Text=e.Message;}}
    public void Dispose(){_stop.Cancel();_stop.Dispose();}
}
