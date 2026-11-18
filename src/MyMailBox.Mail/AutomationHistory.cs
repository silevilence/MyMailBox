using MyMailBox.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyMailBox.Mail;

public sealed partial class MailAutomationService
{
    private long _lastHistoryDay=-1;
    private static string CauseHash(string plan,string cause)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan+"\n"+cause)));
    public Task Cancel(Guid run,CancellationToken token=default)=>Task.Run(()=>
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="""
            UPDATE automation_run SET state='canceled',detail='已停止后续工作。已完成的复制、删除、导出与通知保留原结果。'
            WHERE id=$id AND state IN ('ready','running','paused','failed')
            """;
        c.Parameters.AddWithValue("$id",run.ToString());if(c.ExecuteNonQuery()!=1)throw new InvalidDataException("这次执行已经结束。");
    },token);
    public Task<AutomationAudit> Audit(Guid run,int offset=0,int limit=50,CancellationToken token=default)=>Task.Run(()=>
    {
        if(offset<0 || limit is <1 or >200)throw new ArgumentOutOfRangeException(nameof(limit));
        var summary=Run(run);using var db=_store.Open();using var c=db.CreateCommand();c.Parameters.AddWithValue("$id",run.ToString());
        c.CommandText="""
            SELECT r.definition,r.snapshot,g.report FROM automation_run r LEFT JOIN automation_gate_sample g ON g.run=r.id WHERE r.id=$id
            """;
        AutomationPlan plan;TargetSnapshot snap;ConditionReport? gate;
        using(var r=c.ExecuteReader())
        {
            if(!r.Read())throw new InvalidDataException("执行记录已过保留期。");
            plan=JsonSerializer.Deserialize<AutomationPlan>(r.GetString(0))!;snap=JsonSerializer.Deserialize<TargetSnapshot>(r.GetString(1))!;
            gate=r.IsDBNull(2)?null:JsonSerializer.Deserialize<ConditionReport>(r.GetString(2));
        }
        c.CommandText="SELECT ordinal,state,detail FROM automation_item WHERE run=$id ORDER BY ordinal LIMIT $limit OFFSET $offset";
        c.Parameters.AddWithValue("$limit",limit);c.Parameters.AddWithValue("$offset",offset);var items=new List<AutomationItemLog>();
        using(var r=c.ExecuteReader())while(r.Read())
        {
            int i=r.GetInt32(0);string state=r.GetString(1);bool stopped=summary.State=="canceled" && state=="pending";
            items.Add(new(i,snap.Items[i].Subject,stopped?"canceled":state,stopped?"后续工作已停止。":r.GetString(2)));
        }
        return new AutomationAudit(summary,plan.Version,plan.When.Expression,gate,snap.TriggerScope,snap.TargetScope,snap.At,snap.Items.Count,snap.SelectedBytes,
            plan.Actions.Select(a=>a.Kind.ToString()).Concat(plan.Archive is null?[]:["Archive"]).ToArray(),items,offset);
    },token);
    public async Task<string> ExportAudit(Guid run,CancellationToken token=default)
    {
        var info=await Audit(run,0,1,token);var steps=await Steps(run,token);var r=info.Run;
        var source=r.Cause.Split('/')[0] switch{"manual"=>"manual","daily"=>"daily","event"=>"event",_=>"other"};
        // Export only allowlisted counts, enums and timestamps. Free text can contain addresses, paths and subjects.
        return JsonSerializer.Serialize(new{format=1,at=r.At,state=r.State,source,version=info.Version,condition=info.Observed?.Result.ToString(),
            selection=info.Selected,bytes=info.Bytes,done=r.Done,skipped=r.Skipped,failed=r.Failed,actions=info.Actions,
            steps=steps.Select(s=>new{item=s.Item,step=s.Step,kind=s.Kind.ToString(),state=s.State,attempts=s.Attempts})},new JsonSerializerOptions{WriteIndented=true});
    }
    private void MaintainHistory()
    {
        var now=_clock.GetUtcNow().ToUnixTimeSeconds();long day=now/86400;if(Volatile.Read(ref _lastHistoryDay)==day)return;
        using var db=_store.Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;c.Parameters.AddWithValue("$cut",now-90L*86400);
        c.CommandText="""
            SELECT id,plan,cause FROM automation_run r WHERE at<$cut AND state IN ('done','skipped','canceled')
            AND NOT EXISTS(SELECT 1 FROM automation_item i WHERE i.run=r.id AND i.state='partial')
            """;
        var expired=new List<(string Id,string Hash)>();using(var r=c.ExecuteReader())while(r.Read())expired.Add((r.GetString(0),CauseHash(r.GetString(1),r.GetString(2))));
        foreach(var (id,hash) in expired)
        {
            using var remove=db.CreateCommand();remove.Transaction=tx;remove.CommandText="""
                INSERT OR IGNORE INTO automation_retired VALUES($hash);
                DELETE FROM automation_step WHERE run=$id;
                DELETE FROM automation_item WHERE run=$id;
                DELETE FROM automation_gate_sample WHERE run=$id;
                DELETE FROM automation_lineage WHERE run=$id;
                DELETE FROM automation_run WHERE id=$id;
                """;
            remove.Parameters.AddWithValue("$id",id);remove.Parameters.AddWithValue("$hash",hash);remove.ExecuteNonQuery();
        }
        c.CommandText="DELETE FROM automation_signal WHERE handled AND at<$cut";c.ExecuteNonQuery();tx.Commit();Volatile.Write(ref _lastHistoryDay,day);
    }
}
