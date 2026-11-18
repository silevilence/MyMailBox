using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class AutomationView
{
    private int _auditOffset;
    private AutomationRun SelectedRun()=>_history.SelectedItem as AutomationRun??throw new InvalidDataException("请先选择执行记录。");
    private void HistoryTools(Panel p)
    {
        var buttons=new WrapPanel();
        Button(buttons,"所有任务的历史","auto-all-history",async()=>
        {
            _history.ItemsSource=await _service.Runs(Guid.Empty,_stop.Token);_history.SelectedIndex=_history.ItemCount>0?0:-1;
            if(_history.SelectedItem is not null)await ShowAudit(0);else _status.Text="暂无执行记录。";
        });
        Button(buttons,"查看详情","auto-run-details",()=>ShowAudit(0));
        Button(buttons,"上一批","auto-run-previous",()=>ShowAudit(Math.Max(0,_auditOffset-25)));
        Button(buttons,"下一批","auto-run-next",()=>ShowAudit(_auditOffset+25));
        Button(buttons,"取消后续执行","auto-run-cancel",async()=>{var r=SelectedRun();await _service.Cancel(r.Id,_stop.Token);await ShowAudit(0);});
        Button(buttons,"导出脱敏记录","auto-run-export",async()=>
        {
            var r=SelectedRun();var picker=TopLevel.GetTopLevel(this)?.StorageProvider;
            if(picker is null)return;var file=await picker.SaveFilePickerAsync(new(){Title="导出脱敏任务记录",SuggestedFileName="自动化执行记录.json",DefaultExtension="json"});
            if(file is null)return;string json=await _service.ExportAudit(r.Id,_stop.Token);
            await using var stream=await file.OpenWriteAsync();stream.SetLength(0);await using var writer=new StreamWriter(stream);await writer.WriteAsync(json);_status.Text="已导出脱敏记录。";
        });p.Children.Add(buttons);
        p.Children.Add(new TextBlock{Text="已结束的记录保留 90 天。待恢复和有失败项的记录继续保留。",TextWrapping=Avalonia.Media.TextWrapping.Wrap});
    }
    private async Task ShowAudit(int offset)
    {
        var r=SelectedRun();var a=await _service.Audit(r.Id,offset,25,_stop.Token);
        if(offset>0&&a.Items.Count==0){_status.Text="已到最后一批。";return;}
        _auditOffset=offset;var steps=(await _service.Steps(r.Id,_stop.Token)).Where(s=>s.Item>=offset&&s.Item<offset+25).ToLookup(s=>s.Item);
        _status.Text=RunLabel(a.Run)+$"\n任务配置版本 {a.Version} · 前置条件 {a.Condition} · {(a.Observed is null?"未记录采样":GateLabel(a.Observed.Result))}\n"+
            (a.Observed is {} report?string.Join(" · ",report.Values.Where(v=>a.Condition.Contains(v.Id,StringComparison.Ordinal)).Select(v=>v.Name+" "+(v.Value?.ToString("0.##")??"未知")))+"\n":"")+
            $"统计范围：{a.TriggerScope}\n处理范围：{a.TargetScope} · 快照 {a.SnapshotAt.LocalDateTime:g} · {a.Selected} 封 / {a.Bytes:N0} 字节\n"+
            string.Join("\n",a.Items.Select(i=>$"#{i.Ordinal+1} {i.Subject} · {i.Detail}"+
                string.Concat(steps[i.Ordinal].Select(s=>$"\n  {s.Step+1}. {ActionName(s.Kind)} · 尝试 {s.Attempts} 次 · {s.Detail}"))));
    }
}
