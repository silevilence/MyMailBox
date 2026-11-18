using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class AutomationView : UserControl,IDisposable
{
    private readonly IMailAutomation _service;private readonly CancellationTokenSource _stop=new();
    private readonly StackPanel _panel=new(){MaxWidth=860,Spacing=6};
    private readonly TextBox _name=new(),_filter=new(),_when=new(){Text="unread>=1"};
    private readonly ComboBox _plans=new(),_scope=new();
    private readonly NumericUpDown _hysteresis=new(){Minimum=0,Value=0},_cooldown=new(){Minimum=0,Value=0,FormatString="0"};
    private readonly CheckBox _enabled=new(){Content="启用任务",IsChecked=true};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private AutomationPlan? _loaded;private SearchLibrary? _library;

    internal AutomationView(IMailAutomation service)
    {
        _service=service;_panel.Children.Add(new TextBlock{Text="邮件自动化",FontSize=28,FontWeight=FontWeight.SemiBold});
        _plans.ItemTemplate=new FuncDataTemplate<AutomationPlan>((p,_)=>new TextBlock{Text=p?.Name});
        Field(_panel,"已保存任务",_plans,"auto-plans");var top=new WrapPanel();
        Button(top,"载入任务","auto-load",()=>{if(_plans.SelectedItem is AutomationPlan p)Load(p);return Task.CompletedTask;});
        Button(top,"新建任务","auto-new",()=>{Load(null);return Task.CompletedTask;});_panel.Children.Add(top);
        Field(_panel,"任务名称",_name,"auto-name");Scope(_scope);Field(_panel,"统计范围",_scope,"auto-scope");
        Field(_panel,"统计邮件过滤（可留空）",_filter,"auto-filter");Field(_panel,"运行前置条件",_when,"auto-condition");
        _panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Text="可用 count、unread、logical、cached、disk、free、quota、pending、exists、all、net、ac、minute、last。例：(unread>=10 OR logical>=1048576) AND net=1。"});
        Field(_panel,"迟滞（按各阈值的单位）",_hysteresis,"auto-hysteresis");Field(_panel,"冷却分钟",_cooldown,"auto-cooldown");
        AddTarget();_panel.Children.Add(_enabled);var actions=new WrapPanel();Button(actions,"预览统计","auto-preview",()=>Inspect(false));Button(actions,"记录采样","auto-observe",()=>Inspect(true));
        Button(actions,"保存任务","auto-save",Save);_panel.Children.Add(actions);
        AutomationProperties.SetAutomationId(_status,"auto-status");_panel.Children.Add(_status);Content=new ScrollViewer{Content=_panel};
        AttachedToVisualTree+=async(_,_)=>await Guard(async()=>{_library=await service.Library(_stop.Token);_scope.ItemsSource=_library.Scopes;await Refresh();Load(null);});
    }
    private static void Field(Panel p,string label,Control input,string id)
    {p.Children.Add(new TextBlock{Text=label});AutomationProperties.SetName(input,label);AutomationProperties.SetAutomationId(input,id);p.Children.Add(input);}
    private static void Scope(ComboBox box)=>box.ItemTemplate=new FuncDataTemplate<SearchScope>((s,_)=>new TextBlock{Text=s?.Name});
    private void Button(Panel p,string name,string id,Func<Task> callback)
    {
        var b=new Button{Content=name,Margin=new Avalonia.Thickness(0,0,6,2)};AutomationProperties.SetAutomationId(b,id);b.Click+=async(_,_)=>await Guard(callback);p.Children.Add(b);
    }
    private async Task Guard(Func<Task> work){try{await work();}catch(OperationCanceledException){}catch(Exception e){if(!_stop.IsCancellationRequested)_status.Text=e.Message;}}
    private async Task Refresh(){_plans.ItemsSource=await _service.Plans(_stop.Token);_plans.SelectedItem=_plans.Items.OfType<AutomationPlan>().FirstOrDefault(p=>p.Id==_loaded?.Id);}
    private void Load(AutomationPlan? p)
    {
        LoadTarget(p?.Target);_loaded=p;_name.Text=p?.Name ?? "";_filter.Text=p?.When.Filter ?? "";_when.Text=p?.When.Expression ?? "unread>=1";_enabled.IsChecked=p?.Enabled ?? true;
        _hysteresis.Value=p?.When.Hysteresis ?? 0;_cooldown.Value=p?.When.CooldownMinutes ?? 0;_scope.SelectedItem=_library?.Scopes.FirstOrDefault(s=>s.Account==p?.When.Account && s.Folder==p?.When.Folder);
    }
    private AutomationPlan Draft()
    {
        if(_scope.SelectedItem is not SearchScope scope)throw new InvalidDataException("请重新选择统计范围。");
        return new(_loaded?.Id ?? Guid.NewGuid(),_loaded?.Version ?? 0,_name.Text ?? "",new(scope.Account,scope.Folder,_filter.Text ?? "",_when.Text ?? "",_hysteresis.Value ?? 0,(int)(_cooldown.Value ?? 0)),_enabled.IsChecked==true){Target=Target()};
    }
    private async Task Save(){_loaded=await _service.Save(Draft(),_stop.Token);await Refresh();_status.Text="已保存任务「"+_loaded.Name+"」。";}
    private async Task Inspect(bool observe)
    {
        var p=await _service.Inspect(Draft(),observe,_stop.Token);
        _status.Text=$"{p.At.LocalDateTime:g} · {(p.Result==ConditionResult.True?"满足":p.Result==ConditionResult.False?"未满足":"未知")} · {p.Detail}\n"+
            string.Join("\n",p.Values.Select(v=>$"{v.Name}：{v.Value?.ToString("0.##") ?? "未知"} · {v.Basis} · {v.At.LocalDateTime:T}"));
    }
    public void Dispose(){_stop.Cancel();_stop.Dispose();}
}
