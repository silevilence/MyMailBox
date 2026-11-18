using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Text.Json;

namespace MyMailBox.Mail;

public sealed partial class MailAutomationService
{
    private readonly Action<string>? _notify;
    private const string RunRows="""
        SELECT r.id,r.plan,r.cause,r.state,r.detail,r.at,
        (SELECT count(*) FROM automation_item i WHERE i.run=r.id AND i.state='done'),
        (SELECT count(*) FROM automation_item i WHERE i.run=r.id AND i.state='skipped'),
        (SELECT count(*) FROM automation_item i WHERE i.run=r.id AND i.state IN ('failed','partial')) FROM automation_run r
        """;
    private static AutomationRun ReadRun(SqliteDataReader r)=>new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.GetString(4),
        r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)));
    private AutomationRun Run(Guid id)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText=RunRows+" WHERE r.id=$id";c.Parameters.AddWithValue("$id",id.ToString());using var r=c.ExecuteReader();
        return r.Read()?ReadRun(r):throw new InvalidDataException("任务执行记录不存在。");
    }
    public Task<IReadOnlyList<AutomationRun>> Runs(Guid plan,CancellationToken token=default)=>Task.Run<IReadOnlyList<AutomationRun>>(()=>
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText=RunRows+" WHERE plan=$p ORDER BY at DESC,rowid DESC LIMIT 100";
        c.Parameters.AddWithValue("$p",plan.ToString());using var r=c.ExecuteReader();var runs=new List<AutomationRun>();while(r.Read())runs.Add(ReadRun(r));return runs;
    },token);
    private Guid? Existing(SqliteCommand c)
    {
        c.CommandText="SELECT id FROM automation_run WHERE plan=$p AND (cause=$cause OR state IN ('ready','running','paused','failed')) ORDER BY (cause=$cause) DESC LIMIT 1";
        return c.ExecuteScalar() is string id?Guid.Parse(id):null;
    }
    public Task<AutomationRun> Start(Guid plan,string cause,CancellationToken token=default)=>Start(plan,cause,null,token);
    private async Task<AutomationRun> Start(Guid plan,string cause,Signal? signal,CancellationToken token)
    {
        var definition=(await Plans(token)).FirstOrDefault(p=>p.Id==plan)??throw new InvalidDataException("请先保存任务。");
        if(!definition.Enabled||(definition.Archive is null && definition.Actions.Length==0))throw new InvalidDataException("任务尚未启用或未配置动作。");
        using var db=_store.Open();using var c=db.CreateCommand();c.Parameters.AddWithValue("$p",plan.ToString());c.Parameters.AddWithValue("$cause",cause);
        if(Existing(c) is Guid known)return Run(known);
        var at=_clock.GetUtcNow();var gate=await Inspect(definition,false,token);
        var snapshot=await Select(definition,at,token);
        bool run=gate.Result==ConditionResult.True && snapshot.Items.Count>0;
        string detail=gate.Result==ConditionResult.True?(run?"已冻结目标与执行时刻。":"当前没有可处理邮件。"):gate.Detail+" 前置条件未满足。";
        using var tx=db.BeginTransaction();c.Transaction=tx;
        if(Existing(c) is Guid other){tx.Commit();return Run(other);}
        c.CommandText="SELECT version FROM automation_plan WHERE id=$p";
        if(Convert.ToInt32(c.ExecuteScalar())!=definition.Version)throw new InvalidDataException("准备执行期间配置已变更，请重试。");
        var id=Guid.NewGuid();c.CommandText="INSERT INTO automation_run VALUES($id,$p,$cause,$state,$detail,$at,$def,$snap)";
        c.Parameters.AddWithValue("$id",id.ToString());c.Parameters.AddWithValue("$state",run?"ready":"skipped");
        c.Parameters.AddWithValue("$detail",detail);c.Parameters.AddWithValue("$at",at.ToUnixTimeSeconds());
        c.Parameters.AddWithValue("$def",JsonSerializer.Serialize(definition));c.Parameters.AddWithValue("$snap",JsonSerializer.Serialize(snapshot));c.ExecuteNonQuery();
        if(signal is not null)
        {
            c.CommandText="INSERT INTO automation_lineage VALUES($id,$kind,$origin,$eventAt,$chain)";
            c.Parameters.AddWithValue("$kind",signal.Kind);c.Parameters.AddWithValue("$origin",signal.Origin);c.Parameters.AddWithValue("$eventAt",signal.At);
            c.Parameters.AddWithValue("$chain",JsonSerializer.Serialize(signal.Chain.Append(plan)));c.ExecuteNonQuery();
        }
        if(run)
        {
            c.CommandText="INSERT INTO automation_item VALUES($id,$n,$op,NULL,'pending','等待处理')";
            var n=c.Parameters.Add("$n",SqliteType.Integer);var op=c.Parameters.Add("$op",SqliteType.Text);
            for(int i=0;i<snapshot.Items.Count;i++){n.Value=i;op.Value=Guid.NewGuid().ToString();c.ExecuteNonQuery();}
        }
        tx.Commit();return Run(id);
    }
    public Task Control(Guid run,bool resume,CancellationToken token=default)=>Task.Run(()=>
    {
        using var db=_store.Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
        c.Parameters.AddWithValue("$id",run.ToString());
        c.CommandText=resume?"UPDATE automation_run SET state='ready',detail='等待恢复' WHERE id=$id AND state IN ('paused','failed')":
            "UPDATE automation_run SET state='paused',detail='已暂停后续邮件' WHERE id=$id AND state IN ('ready','running')";
        if(c.ExecuteNonQuery()!=1)throw new InvalidDataException("当前状态不能执行此操作。");
        if(resume){c.CommandText="UPDATE automation_item SET state='pending' WHERE run=$id AND state='failed'";c.ExecuteNonQuery();}tx.Commit();
    },token);

    private FileStream? ArchiveLock()
    {
        try{return new(_store.DatabasePath+".automation.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException e)when((e.HResult&0xffff)is 32 or 33){return null;}
    }
    public async Task<AutomationRun> Advance(Guid run,CancellationToken token=default)
    {
        using var owner=ArchiveLock();if(owner is null)return Run(run);
        using var db=_store.Open();using var c=db.CreateCommand();c.Parameters.AddWithValue("$id",run.ToString());
        c.CommandText="SELECT definition,snapshot FROM automation_run WHERE id=$id AND state IN ('ready','running')";
        AutomationPlan plan;TargetSnapshot snap;using(var r=c.ExecuteReader()){if(!r.Read())return Run(run);plan=JsonSerializer.Deserialize<AutomationPlan>(r.GetString(0))!;snap=JsonSerializer.Deserialize<TargetSnapshot>(r.GetString(1))!;}
        c.CommandText="SELECT definition FROM automation_plan WHERE id=$plan";c.Parameters.AddWithValue("$plan",plan.Id.ToString());
        if(c.ExecuteScalar() is not string current || !JsonSerializer.Deserialize<AutomationPlan>(current)!.Enabled)
        {await Control(run,false,token);return Run(run);}
        c.CommandText="UPDATE automation_run SET state='running' WHERE id=$id AND state IN ('ready','running')";if(c.ExecuteNonQuery()!=1)return Run(run);
        c.CommandText="SELECT ordinal,op,destination FROM automation_item WHERE run=$id AND state='pending' ORDER BY ordinal LIMIT 1";
        int ordinal;Guid op;Guid? dest;
        using(var r=c.ExecuteReader())
        {
            if(!r.Read()){ordinal=-1;op=Guid.Empty;dest=null;}
            else{ordinal=r.GetInt32(0);op=Guid.Parse(r.GetString(1));dest=r.IsDBNull(2)?null:Guid.Parse(r.GetString(2));}
        }
        if(ordinal>=0)
        {
            var mail=snap.Items[ordinal];c.Parameters.AddWithValue("$n",ordinal);
            try
            {
                var (state,note)=plan.Actions.Length>0?await ApplySteps(run,ordinal,plan,snap,mail,token):("done","");
                if(state=="done" && plan.Archive is not null)(state,note)=await ArchiveItem(plan,snap,mail,op,dest,c,token);
                c.CommandText="UPDATE automation_item SET state=$s,detail=$note WHERE run=$id AND ordinal=$n";
                c.Parameters.AddWithValue("$s",state);c.Parameters.AddWithValue("$note",note);c.ExecuteNonQuery();
            }
            catch(OperationCanceledException){throw;}
            catch(Exception e)
            {
                using var tx=db.BeginTransaction();c.Transaction=tx;
                c.CommandText="UPDATE automation_item SET state='failed',detail=$err WHERE run=$id AND ordinal=$n; UPDATE automation_run SET state='failed',detail=$err WHERE id=$id";
                c.Parameters.AddWithValue("$err",e.Message);c.ExecuteNonQuery();PublishOutcome(tx,plan.Id,run,false);tx.Commit();return Run(run);
            }
        }
        using var finish=db.BeginTransaction();c.Transaction=finish;
        c.CommandText="UPDATE automation_run SET state='done',detail='执行完成。' WHERE id=$id AND state='running' AND NOT EXISTS(SELECT 1 FROM automation_item WHERE run=$id AND state='pending')";
        bool finished=c.ExecuteNonQuery()==1;
        if(finished)PublishOutcome(finish,plan.Id,run,Run(run).Failed==0);finish.Commit();
        if(finished)_notify?.Invoke("邮件自动化已完成："+plan.Name);
        return Run(run);
    }
    private async Task<(string,string)> ArchiveItem(AutomationPlan plan,TargetSnapshot snap,TargetMail mail,Guid op,Guid? dest,SqliteCommand c,CancellationToken token)
    {
        var archive=plan.Archive!;
        if(mail.Sent is null)return("skipped","缺少发送日期，无法确定归档年月。");
        if(dest is null)
        {
            string? reason=await RecheckCurrent(plan,snap,mail,plan.Actions.Length>0,token);if(reason is not null)return("skipped",reason);
            var folders=await _store.Folders(token);
            if(mail.Account==archive.Account)
            {
                var source=folders.FirstOrDefault(f=>f.Id==mail.Folder);
                while(source is not null){if(source.Id==archive.Folder)return("skipped","已位于归档目录树。");source=folders.FirstOrDefault(f=>f.Id==source.ParentId);}
            }
            var date=TimeZoneInfo.ConvertTime(mail.Sent.Value,TimeZoneInfo.FindSystemTimeZoneById(archive.Zone));
            async Task<Guid> Child(Guid parent,string name)
            {
                var current=(await _store.Folders(token)).FirstOrDefault(f=>f.ParentId==parent&&f.Name==name&&f.AccountId==archive.Account);
                return current?.Id??await _protocols.Create(archive.Account,parent,name,token);
            }
            dest=await Child(await Child(archive.Folder,date.ToString("yyyy")),date.ToString("MM"));
            c.CommandText="UPDATE automation_item SET destination=$dest WHERE run=$id AND ordinal=$n";c.Parameters.AddWithValue("$dest",dest.Value.ToString());c.ExecuteNonQuery();
        }
        if(archive.Account==mail.Account)
        {
            using var db=_store.Open();using var receipt=db.CreateCommand();receipt.CommandText="SELECT count(*) FROM mail_move_undo WHERE id=$op AND message=$key";receipt.Parameters.AddWithValue("$op",op.ToString());receipt.Parameters.AddWithValue("$key",mail.Key);
            if((long)receipt.ExecuteScalar()!>0)return("done","已核实本地移动回执。");
            if(await RecheckCurrent(plan,snap,mail,plan.Actions.Length>0,token) is {} changed)return("skipped",changed);
            var row=(await _protocols.Apply(new MailActionRequest(mail.Account,mail.Folder,[mail.Key],MailAction.Move,dest){OperationId=op},token)).Single();
            if(!row.Success)throw new InvalidDataException(row.Detail);return("done",row.Detail);
        }
        var transfer=await _protocols.Plan(new(op,mail.Account,mail.Folder,mail.Key,dest.Value,true),token);
        if(transfer.Phase=="planned" && await RecheckCurrent(plan,snap,mail,plan.Actions.Length>0,token) is {} stale)return("skipped",stale);
        var completed=await _protocols.ContinueTransfer(op,null,t=>RecheckCurrent(plan,snap,mail,plan.Actions.Length>0,t),token);
        return completed.Phase switch{"done"=>("done",completed.Detail),"canceled"=>("skipped",completed.Detail),_=>throw new InvalidDataException(completed.Detail)};
    }
    internal async Task Tick(CancellationToken token)
    {
        await DispatchSignals(token);
        foreach(var plan in await Plans(token))
        {
            if(!plan.Enabled)continue;
            if(plan.Archive is {Daily:true} a)
            {
                var local=TimeZoneInfo.ConvertTime(_clock.GetUtcNow(),TimeZoneInfo.FindSystemTimeZoneById(a.Zone));
                if(local.Hour*60+local.Minute>=a.LocalMinute)await Start(plan.Id,"daily/"+local.ToString("yyyy-MM-dd"),token);
            }
            var run=(await Runs(plan.Id,token)).FirstOrDefault(r=>r.State is "ready" or "running");
            if(run is not null)await Advance(run.Id,token);
        }
    }
}

public sealed class AutomationRunner:IDisposable
{
    private readonly CancellationTokenSource _stop=new();private readonly Task _loop;
    public AutomationRunner(MailAutomationService service,Action<string>? notify)
    {
        _loop=Task.Run(async()=>{using var timer=new PeriodicTimer(TimeSpan.FromSeconds(3));
            try{do{try{await service.Tick(_stop.Token);}catch(Exception e)when(e is not OperationCanceledException){notify?.Invoke("自动化暂未完成，请查看任务历史。");}}while(await timer.WaitForNextTickAsync(_stop.Token));}
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}});
    }
    public void Dispose(){_stop.Cancel();_loop.GetAwaiter().GetResult();_stop.Dispose();}
}
