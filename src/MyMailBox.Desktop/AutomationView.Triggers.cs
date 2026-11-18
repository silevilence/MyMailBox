using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class AutomationView
{
    private static readonly Dictionary<string,string> EventNames=new()
    {["received"]="收到新邮件",["sent"]="发送完成",["synced"]="同步完成",["startup"]="应用启动",["unlock"]="桌面解锁",["resume"]="系统恢复",["network"]="网络恢复",["task"]="其他任务结束"};
    private readonly List<AutomationTrigger> _triggers=[];
    private readonly ComboBox _eventKind=new(),_eventAccount=new(),_eventParent=new(),_eventOutcome=new(){ItemsSource=new[]{"不限制结果","上次成功","上次失败"},SelectedIndex=0};
    private readonly ListBox _eventList=new(){MaxHeight=140};
    private void AddTriggers()
    {
        var p=new StackPanel{Spacing=4};_eventKind.ItemsSource=EventNames.Values.ToArray();_eventKind.SelectedIndex=0;
        Field(p,"触发事件",_eventKind,"auto-trigger-kind");Scope(_eventAccount);Field(p,"邮件事件账户（可选）",_eventAccount,"auto-trigger-account");
        _eventParent.ItemTemplate=new FuncDataTemplate<AutomationPlan>((v,_)=>new TextBlock{Text=v?.Name});
        Field(p,"前置任务",_eventParent,"auto-trigger-parent");Field(p,"结果筛选",_eventOutcome,"auto-trigger-result");
        Field(p,"已注册事件",_eventList,"auto-triggers");var actions=new WrapPanel();
        Button(actions,"添加触发","auto-trigger-add",()=>
        {
            var kind=EventNames.Keys.ElementAt(_eventKind.SelectedIndex);
            Guid? source=kind switch{"task"=>(_eventParent.SelectedItem as AutomationPlan)?.Id??throw new InvalidDataException("请选择前置任务。"),
                "received" or "sent" or "synced"=>(_eventAccount.SelectedItem as SearchScope)?.Account,_=>null};
            _triggers.Add(new(kind,source,_eventOutcome.SelectedIndex switch{1=>true,2=>false,_=>(bool?)null}));ShowTriggers();return Task.CompletedTask;
        });
        Button(actions,"移除触发","auto-trigger-remove",()=>{if(_eventList.SelectedIndex>=0)_triggers.RemoveAt(_eventList.SelectedIndex);ShowTriggers();return Task.CompletedTask;});
        p.Children.Add(actions);_panel.Children.Add(new Expander{Header="事件触发",Content=p});
    }
    private void ShowTriggers()=>_eventList.ItemsSource=_triggers.Select(t=>
        EventNames.GetValueOrDefault(t.Kind,t.Kind)+" · "+(t.Source is null?"全部":t.Kind=="task"?_plans.Items.OfType<AutomationPlan>().FirstOrDefault(p=>p.Id==t.Source)?.Name:
        _library?.Scopes.FirstOrDefault(s=>s.Account==t.Source&&s.Folder is null)?.Name)+" · "+(t.PreviousSuccess is null?"不限制结果":t.PreviousSuccess.Value?"上次成功":"上次失败")).ToArray();
    private void LoadTriggers(AutomationPlan? p)
    {
        _eventParent.ItemsSource=_plans.Items.OfType<AutomationPlan>().Where(other=>other.Id!=p?.Id).ToArray();
        _eventAccount.ItemsSource=_library?.Scopes.Where(s=>s.Folder is null).ToArray();_eventAccount.SelectedIndex=0;
        _triggers.Clear();_triggers.AddRange(p?.Triggers??[]);ShowTriggers();
    }
}
