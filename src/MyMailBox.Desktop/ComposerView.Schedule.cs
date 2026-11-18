using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class ComposerView
{
    private readonly CalendarDatePicker _sendDate=new(){SelectedDate=DateTime.Today};
    private readonly TimePicker _sendTime=new(){ClockIdentifier="24HourClock",SelectedTime=DateTime.Now.AddHours(1).TimeOfDay};
    private readonly ComboBox _zone=new(){ItemsSource=TimeZoneInfo.GetSystemTimeZones().Select(z=>z.Id).ToArray(),SelectedItem=TimeZoneInfo.Local.Id};
    private readonly ComboBox _missed=new(){ItemsSource=new[]{"恢复运行后发送","迟到超过一分钟时等待确认"},SelectedIndex=0};
    private readonly TextBlock _planStatus=new(){TextWrapping=TextWrapping.Wrap};
    private SendPlan? _plan;private Guid? _planId;private int _planRead;

    private void AddSendPlan()
    {
        var panel=new StackPanel{Spacing=4};
        panel.Children.Add(new TextBlock{Text="设备和 MyMailBox 需要保持运行，时间按所选时区计算。",TextWrapping=TextWrapping.Wrap});
        foreach(var (control,name) in new (Control,string)[]{(_sendDate,"日期"),(_sendTime,"时间"),(_zone,"时区"),(_missed,"错过时")})
        {panel.Children.Add(new TextBlock{Text=name});AutomationProperties.SetName(control,name);panel.Children.Add(control);}
        var buttons=new WrapPanel();Button(buttons,"保存或修改预约","compose-schedule",()=>Schedule(false));
        Button(buttons,"延迟 30 秒发送（期间可撤销）","compose-delay",()=>Schedule(true));
        Button(buttons,"取消尚未发出的计划","compose-cancel-plan",async()=>{
            if(_draft is {} d){await _page.Outbox.Cancel(d.Id,_stop.Token);await ReadPlan(d.Id);_status.Text="已取消计划，草稿可继续编辑。";}});
        Button(buttons,"刷新计划","compose-refresh-plan",async()=>{if(_draft is {} d)await ReadPlan(d.Id);});panel.Children.Add(buttons);
        panel.Children.Add(_planStatus);_form.Children.Add(new Expander{Header="稍后发送与撤销窗口",Content=panel});
    }
    private async Task ReadPlan(Guid id)
    {
        int version=++_planRead;_planId=id;_plan=null;
        try
        {
            var plan=await _page.Outbox.GetSendPlan(id,_stop.Token);if(_disposed || version!=_planRead || _draft?.Id!=id)return;
            _plan=plan;_planStatus.Text="当前草稿没有待执行的预约。";
            if(plan is null)return;
            var time=TimeZoneInfo.ConvertTime(plan.Due,TimeZoneInfo.FindSystemTimeZoneById(plan.Zone));
            _zone.SelectedItem=plan.Zone;_sendDate.SelectedDate=time.Date;_sendTime.SelectedTime=time.TimeOfDay;_missed.SelectedIndex=(int)plan.Missed;
            _planStatus.Text=$"已保留计划 {time:yyyy-MM-dd HH:mm:ss zzz} · {plan.Zone}。编辑正文后须更新预约。";
        }
        catch(Exception e){if(!_disposed && version==_planRead)_planStatus.Text=e.Message;}
    }
    private async Task Schedule(bool delay)
    {
        await Flush();var d=_draft ?? throw new InvalidDataException("请先创建草稿。");
        if(d.Version==0)_draft=d=_page.Drafts.Save(d);
        var zone=TimeZoneInfo.FindSystemTimeZoneById(_zone.SelectedItem as string ?? TimeZoneInfo.Local.Id);
        DateTimeOffset due;
        if(delay)due=DateTimeOffset.Now.AddSeconds(30);
        else
        {
            if(_sendDate.SelectedDate is not DateTime day || _sendTime.SelectedTime is not TimeSpan time)throw new InvalidDataException("请填写日期和时间。");
            var local=DateTime.SpecifyKind(day.Date+time,DateTimeKind.Unspecified);
            if(zone.IsInvalidTime(local)||zone.IsAmbiguousTime(local))throw new InvalidDataException("此时区的时刻无效或重复，请调整时间或选 UTC。");
            due=new DateTimeOffset(local,zone.GetUtcOffset(local));
        }
        await _page.Outbox.Schedule(d.Id,d.Account,d.Message(),new(due,zone.Id,(MissedSend)_missed.SelectedIndex,_plan?.Revision ?? 0,d.Version),_stop.Token);
        await ReadPlan(d.Id);_status.Text=delay?"已保留 30 秒撤销窗口，邮件尚未发出。":"发送计划已保存。";
    }
}
