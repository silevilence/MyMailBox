using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class AutomationView
{
    private readonly CheckBox _archiveOn=new(){Content="归档到本地年 / 月目录"},_daily=new(){Content="每天运行"};
    private readonly ComboBox _destination=new(),_history=new();
    private readonly TimePicker _time=new(){ClockIdentifier="24HourClock"};
    private readonly TextBox _zone=new(){Text=TimeZoneInfo.Local.Id};
    private IReadOnlyList<MailFolder> _destinations=[];

    private void AddArchive()
    {
        var p=new StackPanel{Spacing=4};
        p.Children.Add(_archiveOn);
        _destination.ItemTemplate=new FuncDataTemplate<SearchScope>((s,_)=>new TextBlock{Text=s?.Name});
        Field(p,"本地归档根目录",_destination,"auto-archive-folder");p.Children.Add(_daily);
        Field(p,"每日执行时间",_time,"auto-daily-time");Field(p,"时区",_zone,"auto-zone");
        var actions=new WrapPanel();
        Button(actions,"预演本次归档","auto-archive-preview",async()=>
        {
            var draft=Draft();
            var gate=await _service.Inspect(draft,false,_stop.Token);
            var snapshot=await _service.Select(draft,token:_stop.Token);
            _status.Text=$"前置条件：{GateLabel(gate.Result)}。已选 {snapshot.Items.Count} 封，共 {snapshot.SelectedBytes:N0} 字节。\n"+
                string.Join("\n",snapshot.Items.Take(20).Select(m=>m.Subject+" → "+(m.Sent is {} at?TimeZoneInfo.ConvertTime(at,TimeZoneInfo.FindSystemTimeZoneById(_zone.Text!)).ToString("yyyy/MM"):"日期未知，将跳过")));
        });
        Button(actions,"立即执行已保存任务","auto-start",async()=>
        {
            var plan=SavedId();var run=await _service.Start(plan,"manual/"+Guid.NewGuid(),_stop.Token);await History(run.Id);
        });
        p.Children.Add(actions);_history.ItemTemplate=new FuncDataTemplate<AutomationRun>((r,_)=>new TextBlock{Text=r is null?"":RunLabel(r)});
        Field(p,"最近执行",_history,"auto-history");
        actions=new WrapPanel();Button(actions,"刷新历史","auto-history-refresh",()=>History((_history.SelectedItem as AutomationRun)?.Id));
        Button(actions,"暂停","auto-pause",()=>Change(false));Button(actions,"继续或重试","auto-resume",()=>Change(true));p.Children.Add(actions);
        _panel.Children.Add(new Expander{Header="动作、时间与执行记录",Content=p});
    }
    private Guid SavedId()=>_loaded?.Id??throw new InvalidDataException("请先保存或载入任务。");
    private static string GateLabel(ConditionResult result)=>result switch{ConditionResult.True=>"满足",ConditionResult.False=>"未满足",_=>"未知"};
    private static string RunLabel(AutomationRun run)
    {
        var state=run.State switch{"ready"=>"等待执行","running"=>"运行中","paused"=>"已暂停","done"=>"已完成","skipped"=>"已跳过","failed"=>"失败",_=>run.State};
        return $"{run.At.LocalDateTime:g} · {state} · {run.Cause} · 完成 {run.Done} / 跳过 {run.Skipped} / 失败 {run.Failed} · {run.Detail}";
    }
    private async Task History(Guid? selected)
    {
        var runs=await _service.Runs(SavedId(),_stop.Token);_history.ItemsSource=runs;
        _history.SelectedItem=runs.FirstOrDefault(r=>r.Id==selected)??runs.FirstOrDefault();
        if(_history.SelectedItem is not AutomationRun run){_status.Text="暂无执行记录。";return;}
        var steps=await _service.Steps(run.Id,_stop.Token);
        _status.Text=RunLabel(run)+"\n"+string.Join("\n",steps.Take(100).Select(s=>$"邮件 {s.Item+1} / 步骤 {s.Step+1} · {ActionName(s.Kind)} · 尝试 {s.Attempts} 次 · {s.Detail}"));
    }
    private async Task Change(bool resume)
    {
        if(_history.SelectedItem is not AutomationRun r)throw new InvalidDataException("请先选择执行记录。");
        await _service.Control(r.Id,resume,_stop.Token);await History(r.Id);
    }
    private void LoadArchive(AutomationPlan? plan)
    {
        var a=plan?.Archive;_archiveOn.IsChecked=a is not null;_daily.IsChecked=a?.Daily??true;
        _destination.ItemsSource=_library?.Scopes.Where(s=>_destinations.Any(f=>f.AccountId==s.Account&&f.Id==s.Folder)).ToArray();
        _destination.SelectedItem=_destination.Items.OfType<SearchScope>().FirstOrDefault(s=>s.Account==a?.Account&&s.Folder==a?.Folder);
        _time.SelectedTime=TimeSpan.FromMinutes(a?.LocalMinute??540);_zone.Text=a?.Zone??TimeZoneInfo.Local.Id;_history.ItemsSource=null;
    }
    private ArchivePlan? Archive()
    {
        if(_archiveOn.IsChecked!=true)return null;
        if(_destination.SelectedItem is not SearchScope{Account:{} account,Folder:{} folder}||_time.SelectedTime is not {} time)
            throw new InvalidDataException("请选择本地归档目录和运行时刻。");
        return new(account,folder,_daily.IsChecked==true,(int)time.TotalMinutes,_zone.Text??TimeZoneInfo.Local.Id);
    }
}
