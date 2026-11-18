using MyMailBox.Contracts;
using System.Text.Json;

namespace MyMailBox.Mail;

internal sealed partial class OutboxStore
{
    internal SendPlan? Plan(Guid id)
    {
        using var db=store.Open();using var c=Command(db,"SELECT due,zone,missed,revision,draft_version FROM send_plan JOIN outbox USING(id) WHERE id=$id AND state IN ('scheduled','held')",("id",id.ToString()));
        using var r=c.ExecuteReader();return r.Read()?new(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),r.GetString(1),(MissedSend)r.GetInt32(2),r.GetInt32(3),r.GetInt32(4)):null;
    }
    internal void Schedule(Guid id,Guid account,string subject,byte[] raw,string[] recipients,string hash,SendPlan plan)
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById(plan.Zone);
        if(plan.Due<=DateTimeOffset.UtcNow || !Enum.IsDefined(plan.Missed))throw new InvalidDataException("请选择将来的时刻与有效的错过策略。");
        using var db=store.Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
        c.Parameters.AddWithValue("$id",id.ToString());c.Parameters.AddWithValue("$a",account.ToString());
        c.CommandText="SELECT version FROM compose_draft WHERE id=$id AND account=$a AND NOT submitted";
        if(Convert.ToInt32(c.ExecuteScalar())!=plan.DraftVersion || plan.DraftVersion==0)throw new InvalidDataException("请先保存当前草稿再安排发送。");
        c.CommandText="SELECT state,coalesce(revision,0) FROM outbox LEFT JOIN send_plan USING(id) WHERE id=$id";
        int revision=0;using(var r=c.ExecuteReader())if(r.Read())
        {
            var state=r.GetString(0);revision=r.GetInt32(1);
            if(revision==0 || !(state is "scheduled" or "held" && revision==plan.Revision || state=="canceled" && plan.Revision==0))
                throw new InvalidDataException("发送计划已更新、取消或开始执行，请刷新。");
        }
        else if(plan.Revision!=0)throw new InvalidDataException("原发送计划已不存在。");
        var due=(plan.Due.ToUnixTimeMilliseconds()+999)/1000;
        c.Parameters.AddWithValue("$due",due);c.Parameters.AddWithValue("$subject",subject);c.Parameters.AddWithValue("$raw",raw);
        c.Parameters.AddWithValue("$hash",hash);c.Parameters.AddWithValue("$remaining",JsonSerializer.Serialize(recipients));
        c.Parameters.AddWithValue("$detail","预约："+TimeZoneInfo.ConvertTime(plan.Due,zone).ToString("yyyy-MM-dd HH:mm:ss zzz")+" · "+zone.Id+
            (plan.Missed==MissedSend.SendWhenAvailable?" · 恢复后发送":" · 迟到超过一分钟时等待确认"));
        c.CommandText="""
            INSERT INTO outbox(id,account,subject,raw,request_hash,remaining,state,detail,created,due)
            VALUES($id,$a,$subject,$raw,$hash,$remaining,'scheduled',$detail,unixepoch(),$due)
            ON CONFLICT(id) DO UPDATE SET subject=excluded.subject,raw=excluded.raw,request_hash=excluded.request_hash,remaining=excluded.remaining,
                state='scheduled',detail=excluded.detail,due=excluded.due
            """;
        c.ExecuteNonQuery();c.CommandText="INSERT OR REPLACE INTO send_plan VALUES($id,$zone,$missed,$rev,$version)";
        c.Parameters.AddWithValue("$zone",plan.Zone);c.Parameters.AddWithValue("$missed",(int)plan.Missed);c.Parameters.AddWithValue("$rev",revision+1);
        c.Parameters.AddWithValue("$version",plan.DraftVersion);c.ExecuteNonQuery();tx.Commit();
    }

    internal void ActivateDue(Guid id)
    {
        using var db=store.Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
        c.Parameters.AddWithValue("$id",id.ToString());c.CommandText="""
            SELECT (missed=1 AND due<unixepoch()-60),draft_version,coalesce(d.version,0)
            FROM send_plan s JOIN outbox o ON o.id=s.id LEFT JOIN compose_draft d ON d.id=s.id
            WHERE s.id=$id AND o.state='scheduled' AND due<=unixepoch()
            """;
        bool late,edited;using(var r=c.ExecuteReader()){if(!r.Read())return;late=r.GetBoolean(0);edited=r.GetInt32(1)!=r.GetInt32(2);}
        c.CommandText=late||edited?"UPDATE outbox SET state='held',detail=$detail WHERE id=$id":
            "UPDATE outbox SET state='pending',detail='预约已到期，开始投递。' WHERE id=$id; UPDATE compose_draft SET submitted=1 WHERE id=$id";
        c.Parameters.AddWithValue("$detail",edited?"草稿在预约后已修改，请确认当前内容并更新计划。":"已错过预约时间，请确认并重新安排。");
        c.ExecuteNonQuery();tx.Commit();
    }
}
