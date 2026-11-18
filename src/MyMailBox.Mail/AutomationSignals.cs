using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Text.Json;

namespace MyMailBox.Mail;

internal static class AutomationSignals
{
    internal static void Initialize(SqliteCommand c)
    {
        c.CommandText="""
            CREATE TABLE IF NOT EXISTS automation_sub(plan TEXT NOT NULL,kind TEXT NOT NULL,source TEXT NOT NULL,prior INTEGER,PRIMARY KEY(plan,kind,source));
            CREATE TABLE IF NOT EXISTS automation_signal(plan TEXT NOT NULL,kind TEXT NOT NULL,origin TEXT NOT NULL,event TEXT NOT NULL,at INTEGER NOT NULL,
                chain TEXT NOT NULL,outcome INTEGER,handled INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(plan,kind,origin,event));
            CREATE INDEX IF NOT EXISTS automation_signal_due ON automation_signal(handled,at);
            CREATE TABLE IF NOT EXISTS automation_lineage(run TEXT PRIMARY KEY,kind TEXT NOT NULL,origin TEXT NOT NULL,at INTEGER NOT NULL,chain TEXT NOT NULL);
            """;
        c.ExecuteNonQuery();
    }
    internal static void Post(SqliteMailStore store,string kind,string origin,string id)
    {
        using var db=store.Open();using var tx=db.BeginTransaction();Post(tx,kind,origin,id);tx.Commit();
    }
    internal static void Post(SqliteTransaction tx,string kind,string origin,string id,string chain="[]",bool? success=null)
    {
        using var c=tx.Connection!.CreateCommand();c.Transaction=tx;c.CommandText="""
            INSERT OR IGNORE INTO automation_signal(plan,kind,origin,event,at,chain,outcome)
                SELECT plan,kind,$origin,$id,unixepoch(),$chain,$ok FROM automation_sub WHERE kind=$kind AND (source='' OR source=$origin)
            """;
        c.Parameters.AddWithValue("$origin",origin);c.Parameters.AddWithValue("$id",id);c.Parameters.AddWithValue("$kind",kind);
        c.Parameters.AddWithValue("$chain",chain);c.Parameters.AddWithValue("$ok",(object?)success??DBNull.Value);c.ExecuteNonQuery();
    }
}

public sealed partial class MailAutomationService
{
    private sealed record Signal(long Row,Guid Plan,string Kind,string Origin,string Event,long At,Guid[] Chain,bool? Outcome);
    private static void SaveTriggers(AutomationPlan p,SqliteTransaction tx)
    {
        using var c=tx.Connection!.CreateCommand();c.Transaction=tx;c.Parameters.AddWithValue("$plan",p.Id.ToString());
        c.CommandText="DELETE FROM automation_sub WHERE plan=$plan";c.ExecuteNonQuery();
        if(p.Triggers.GroupBy(t=>(t.Kind,t.Source)).Any(g=>g.Count()>1))throw new InvalidDataException("重复的触发条件。");
        foreach(var t in p.Triggers)
        {
            if(t.Kind is not ("received" or "sent" or "synced" or "startup" or "unlock" or "resume" or "network" or "task"))throw new InvalidDataException("未知事件来源。");
            if(t.Kind=="task")
            {
                if(t.Source is not {} source||source==p.Id)throw new InvalidDataException("请选择其他前置任务。");
                c.CommandText="SELECT count(*) FROM automation_plan WHERE id=$parent";c.Parameters.AddWithValue("$parent",source.ToString());
                if((long)c.ExecuteScalar()! ==0)throw new InvalidDataException("前置任务不存在。");
                c.Parameters.RemoveAt("$parent");
            }
            using var insert=tx.Connection.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT INTO automation_sub VALUES($p,$k,$s,$v)";
            insert.Parameters.AddWithValue("$p",p.Id.ToString());insert.Parameters.AddWithValue("$k",t.Kind);
            insert.Parameters.AddWithValue("$s",t.Source?.ToString()??"");insert.Parameters.AddWithValue("$v",(object?)t.PreviousSuccess??DBNull.Value);insert.ExecuteNonQuery();
        }
        c.CommandText="""
            WITH RECURSIVE parents(id) AS (SELECT source FROM automation_sub WHERE plan=$plan AND kind='task'
                UNION SELECT s.source FROM automation_sub s JOIN parents p ON p.id=s.plan WHERE s.kind='task')
            SELECT count(*) FROM parents WHERE id=$plan
            """;
        if((long)c.ExecuteScalar()!>0)throw new InvalidDataException("任务依赖形成闭环。");
        // Events queued under a previous configuration must not execute newly enabled actions.
        c.CommandText="UPDATE automation_signal SET handled=1 WHERE plan=$plan AND handled=0";c.ExecuteNonQuery();
    }
    public Task SystemEvent(string kind,CancellationToken token=default)=>Task.Run(()=>
    {
        if(kind is not ("startup" or "unlock" or "resume" or "network"))throw new InvalidDataException("此入口仅接收系统事件。");
        AutomationSignals.Post(_store,kind,"desktop",Guid.NewGuid().ToString());
    },token);
    private static void PublishOutcome(SqliteTransaction tx,Guid plan,Guid run,bool success)
    {
        using var c=tx.Connection!.CreateCommand();c.Transaction=tx;c.CommandText="INSERT OR REPLACE INTO automation_outcome VALUES($plan,$ok,unixepoch())";
        c.Parameters.AddWithValue("$plan",plan.ToString());c.Parameters.AddWithValue("$ok",success);c.ExecuteNonQuery();
        c.CommandText="SELECT chain FROM automation_lineage WHERE run=$run";c.Parameters.AddWithValue("$run",run.ToString());
        var chain=c.ExecuteScalar() as string??JsonSerializer.Serialize(new[]{plan});
        c.CommandText="SELECT json_extract(definition,'$.Name'),json_extract(definition,'$.Target.Account') FROM automation_run WHERE id=$run";
        string name;string? account;using(var r=c.ExecuteReader()){r.Read();name=r.GetString(0);account=r.IsDBNull(1)?null:r.GetString(1);}
        ActivityCenter.Add(c,"task/"+run+"/"+success,success?"task":"error",account,success?"任务已完成："+name:"任务执行失败："+name,"可查看选择快照及逐步结果。","task",run.ToString());
        AutomationSignals.Post(tx,"task",plan.ToString(),run+"/"+(success?"ok":"failed"),chain,success);
    }
    internal async Task DispatchSignals(CancellationToken token)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="""
            SELECT rowid,plan,kind,origin,event,at,chain,outcome FROM automation_signal WHERE handled=0 ORDER BY at,rowid LIMIT 32
            """;
        var batch=new List<Signal>();using(var r=c.ExecuteReader())while(r.Read())batch.Add(new(r.GetInt64(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),
            r.GetString(4),r.GetInt64(5),JsonSerializer.Deserialize<Guid[]>(r.GetString(6))!,r.IsDBNull(7)?null:r.GetBoolean(7)));
        foreach(var e in batch)
        {
            token.ThrowIfCancellationRequested();
            using var sub=db.CreateCommand();sub.Parameters.AddWithValue("$p",e.Plan.ToString());sub.Parameters.AddWithValue("$k",e.Kind);sub.Parameters.AddWithValue("$origin",e.Origin);
            sub.Parameters.AddWithValue("$result",(object?)e.Outcome??DBNull.Value);
            sub.CommandText="""
                SELECT count(*) FROM automation_sub JOIN automation_plan p ON p.id=plan WHERE plan=$p AND kind=$k AND (source='' OR source=$origin)
                AND json_extract(p.definition,'$.Enabled') AND (prior IS NULL OR prior=coalesce($result,(SELECT success FROM automation_outcome WHERE id=$p)))
                """;
            if(e.Chain.Length<8 && !e.Chain.Contains(e.Plan) && (long)sub.ExecuteScalar()!>0)
            {
                // A burst shares one persisted cause per source, plan and 10-second window.
                string cause="event/"+e.Kind+"/"+e.Origin+"/"+(e.Kind=="task"?e.Event:(e.At/10).ToString());
                if(e.Kind!="task")
                {
                    sub.CommandText="SELECT cause FROM automation_run r JOIN automation_lineage l ON l.run=r.id WHERE r.plan=$p AND l.kind=$k AND l.origin=$origin AND l.at BETWEEN $since AND $now ORDER BY l.at DESC LIMIT 1";
                    sub.Parameters.AddWithValue("$since",e.At-9);sub.Parameters.AddWithValue("$now",e.At);
                    cause=sub.ExecuteScalar() as string??cause;
                }
                try{await Start(e.Plan,cause,e,token);}
                catch(InvalidDataException ex){_notify?.Invoke("自动化事件未执行："+ex.Message);}
            }
            sub.CommandText="UPDATE automation_signal SET handled=1 WHERE rowid=$row";sub.Parameters.AddWithValue("$row",e.Row);sub.ExecuteNonQuery();
        }
    }
}
