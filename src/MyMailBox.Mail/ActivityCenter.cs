using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ActivityCenter(SqliteMailStore store) : IActivityCenter
{
    private static readonly string[] Kinds=["received","sync","error","reminder","task"];
    internal static void Initialize(SqliteCommand c)
    {
        c.CommandText="""
            CREATE TABLE IF NOT EXISTS activity(id INTEGER PRIMARY KEY,stamp TEXT UNIQUE,kind TEXT NOT NULL,account TEXT,title TEXT NOT NULL,detail TEXT NOT NULL,
                route TEXT NOT NULL,ref TEXT NOT NULL,folder TEXT,at INTEGER NOT NULL,pending INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS activity_pending ON activity(pending,id);
            CREATE TABLE IF NOT EXISTS activity_policy(id INTEGER PRIMARY KEY CHECK(id=1),quiet INTEGER,private INTEGER,presenter TEXT);
            INSERT OR IGNORE INTO activity_policy VALUES(1,0,1,'builtin');
            CREATE TABLE IF NOT EXISTS activity_filter(kind TEXT,account TEXT,enabled INTEGER,PRIMARY KEY(kind,account));
            """;
        c.ExecuteNonQuery();
    }
    internal static void Add(SqliteCommand parent,string stamp,string kind,string? account,string title,string detail,string route,string reference,string? folder=null,bool announce=true)
    {
        using var c=parent.Connection!.CreateCommand();c.Transaction=parent.Transaction;c.CommandText="""
            INSERT OR IGNORE INTO activity(stamp,kind,account,title,detail,route,ref,folder,at,pending)
            VALUES($stamp,$k,$a,$title,$detail,$route,$ref,$folder,unixepoch(),$on)
            """;
        c.Parameters.AddWithValue("$stamp",stamp);c.Parameters.AddWithValue("$k",kind);c.Parameters.AddWithValue("$a",(object?)account??DBNull.Value);
        c.Parameters.AddWithValue("$title",title);c.Parameters.AddWithValue("$detail",detail);c.Parameters.AddWithValue("$route",route);
        c.Parameters.AddWithValue("$ref",reference);c.Parameters.AddWithValue("$folder",(object?)folder??DBNull.Value);c.Parameters.AddWithValue("$on",announce);c.ExecuteNonQuery();
    }
    public void RecordError(string detail)
    {
        using var db=store.Open();using var c=db.CreateCommand();Add(c,"error/"+Guid.NewGuid(),"error",null,"后台操作未完成",detail,"none","");
    }
    private const string Columns="id,kind,account,title,detail,route,ref,folder,at";
    private static ActivityEntry Row(SqliteDataReader r)=>new(r.GetInt64(0),r.GetString(1),r.IsDBNull(2)?null:Guid.Parse(r.GetString(2)),r.GetString(3),r.GetString(4),
        r.GetString(5),r.GetString(6),r.IsDBNull(7)?null:Guid.Parse(r.GetString(7)),DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)));
    public Task<IReadOnlyList<ActivityEntry>> Recent(int offset=0,int limit=50,CancellationToken token=default)=>Task.Run<IReadOnlyList<ActivityEntry>>(()=>
    {
        if(offset<0||limit is <1 or >200)throw new ArgumentOutOfRangeException(nameof(limit));using var db=store.Open();using var c=db.CreateCommand();
        c.CommandText="SELECT "+Columns+" FROM activity ORDER BY id DESC LIMIT $n OFFSET $o";c.Parameters.AddWithValue("$n",limit);c.Parameters.AddWithValue("$o",offset);
        using var r=c.ExecuteReader();var rows=new List<ActivityEntry>();while(r.Read())rows.Add(Row(r));return rows;
    },token);
    public Task<NoticePolicy> Policy(CancellationToken token=default)=>Task.Run(()=>
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT quiet,private,presenter FROM activity_policy WHERE id=1";
        using var r=c.ExecuteReader();r.Read();return new NoticePolicy(r.GetBoolean(0),r.GetBoolean(1),r.GetString(2));
    },token);
    public Task Configure(NoticePolicy settings,CancellationToken token=default)=>Task.Run(()=>
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="UPDATE activity_policy SET quiet=$q,private=$p,presenter=$sink WHERE id=1";
        c.Parameters.AddWithValue("$q",settings.Quiet);c.Parameters.AddWithValue("$p",settings.HideWhileLocked);c.Parameters.AddWithValue("$sink",settings.Presenter);c.ExecuteNonQuery();
    },token);
    public Task<bool> Enabled(string kind,Guid? account,CancellationToken token=default)=>Task.Run(()=>
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT enabled FROM activity_filter WHERE kind=$k AND account IN ('',$a) ORDER BY length(account) DESC LIMIT 1";
        c.Parameters.AddWithValue("$k",kind);c.Parameters.AddWithValue("$a",account?.ToString()??"");return c.ExecuteScalar() is not 0L;
    },token);
    public Task SetEnabled(string kind,Guid? account,bool enabled,CancellationToken token=default)=>Task.Run(()=>
    {
        if(!Kinds.Contains(kind))throw new InvalidDataException("未知活动类别。");
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="INSERT OR REPLACE INTO activity_filter VALUES($k,$a,$v)";
        c.Parameters.AddWithValue("$k",kind);c.Parameters.AddWithValue("$a",account?.ToString()??"");c.Parameters.AddWithValue("$v",enabled);c.ExecuteNonQuery();
    },token);
    public Task<ActivityLink> Resolve(long entry,CancellationToken token=default)=>Task.Run(()=>
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT "+Columns+" FROM activity WHERE id=$id";c.Parameters.AddWithValue("$id",entry);
        ActivityEntry a;using(var r=c.ExecuteReader()){if(!r.Read())throw new InvalidDataException("活动记录已失效。");a=Row(r);}
        c.Parameters.AddWithValue("$ref",a.Reference);c.Parameters.AddWithValue("$account",a.Account?.ToString()??"");
        if(a.Account is not null && a.Route is "account" or "mail")
        {
            c.CommandText="SELECT count(*) FROM accounts WHERE id=$account AND enabled AND NOT removed";if((long)c.ExecuteScalar()! ==0)throw new InvalidDataException("关联账户已停用或删除。");
        }
        if(a.Route=="mail")
        {
            c.Parameters.AddWithValue("$folder",a.Folder!.Value.ToString());bool remote=uint.TryParse(a.Reference,out uint uid);
            c.CommandText=remote?"""
                SELECT s.subject,s.sender,s.size,f.path,i.validity,s.message,f.id FROM imap_summaries s JOIN imap_folders i ON i.id=s.folder JOIN folders f ON f.id=i.id
                WHERE s.folder=$folder AND s.uid=$ref AND i.account=$account AND NOT s.missing
                """:"""
                SELECT m.subject,m.sender,m.size,f.path,NULL,m.id,f.id FROM messages m JOIN folders f ON f.id=m.folder_id WHERE m.id=$ref AND f.account_id=$account
                """;
            using var row=c.ExecuteReader();if(!row.Read())throw new InvalidDataException("关联邮件已删除或移出原位置。");
            var hit=new SearchHit(remote?a.Folder+"/"+uid:a.Reference,a.Account!.Value,Guid.Parse(row.GetString(6)),row.GetString(3),row.GetString(0),row.GetString(1),null,row.GetInt64(2),
                row.IsDBNull(5)?null:Guid.Parse(row.GetString(5)),remote?uid:null,row.IsDBNull(4)?null:(uint)row.GetInt64(4));
            return new ActivityLink("mail",a.Account,null,hit);
        }
        if(a.Route=="account")return new ActivityLink(a.Route,a.Account,null);
        var table=a.Route switch{"task"=>"automation_run","reminder"=>"reminders","schedule"=>"schedules",_=>throw new InvalidDataException("该活动没有关联页面。")};
        c.CommandText="SELECT count(*) FROM "+table+" WHERE id=$ref";
        if((long)c.ExecuteScalar()! ==0)throw new InvalidDataException("关联记录已失效。");
        return new ActivityLink(a.Route,a.Account,Guid.Parse(a.Reference));
    },token);
    public async Task<int> Dispatch(Func<bool> locked,Func<IReadOnlyList<NotificationPresenter>> presenters,CancellationToken token=default)
    {
        using var db=store.Open();using var c=db.CreateCommand();var rows=new List<ActivityEntry>();
        using(var tx=db.BeginTransaction())
        {
            c.Transaction=tx;c.CommandText="SELECT "+Columns+" FROM activity WHERE pending ORDER BY id LIMIT 100";using(var r=c.ExecuteReader())while(r.Read())rows.Add(Row(r));
            if(rows.Count==0){tx.Commit();return 0;}
            c.CommandText="UPDATE activity SET pending=0 WHERE pending AND id<=$last";c.Parameters.AddWithValue("$last",rows[^1].Id);c.ExecuteNonQuery();tx.Commit();c.Transaction=null;
        }
        var policy=await Policy(token);if(policy.Quiet)return 0;int shown=0;
        foreach(var g in rows.GroupBy(r=>(r.Kind,r.Account)))
        {
            if(!await Enabled(g.Key.Kind,g.Key.Account,token))continue;
            var last=g.Last();var sinks=presenters();var sink=sinks.FirstOrDefault(p=>p.Id==policy.Presenter)??sinks.FirstOrDefault(p=>p.Id=="builtin");
            if(sink is null)continue;
            string text=policy.HideWhileLocked&&locked()?"有新的邮件活动。":g.Count()==1?last.Title:$"{last.Title} 等 {g.Count()} 项活动。";
            try{await sink.Show(new(last.Id,g.Key.Kind,g.Count(),text),token);shown++;}
            catch(Exception e)when(e is not OperationCanceledException){Add(c,"presenter/"+last.Id,"error",null,"通知显示失败","原事件保留于活动中心。","none","",announce:false);}
        }
        return shown;
    }
}
