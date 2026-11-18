using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

internal sealed record MailCounts(long Count,long Unread,long Logical,long Cached,long Unknown,long Outside,bool Complete,long? Pending,decimal? Quota,DateTimeOffset At,string Basis);

public sealed partial class MailSearchService
{
    private void PreparePreview(SqliteConnection db,CancellationToken token)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="""
            CREATE TEMP TABLE search_meta AS SELECT * FROM main.search_meta LIMIT 0;
            CREATE UNIQUE INDEX temp.preview_meta_key ON search_meta(id);
            CREATE TEMP TABLE derived_search_value(field TEXT,id TEXT,source TEXT,value TEXT,PRIMARY KEY(field,id));
            """;
        cmd.ExecuteNonQuery();_metadata.Prepare(token,db);
    }
    internal MailCounts CountScope(AutomationCondition scope,DateTimeOffset now,CancellationToken token)
    {
        using var db=_store.Open(true);PreparePreview(db,token);
        using var c=db.CreateCommand();var unavailable=new HashSet<string>();
        var filter=string.IsNullOrWhiteSpace(scope.Filter)?"1":Compile(db,c,ConditionTree.Parse(scope.Filter),unavailable,token,preview:true);
        c.Parameters.AddWithValue("$a",(object?)scope.Account?.ToString()??DBNull.Value);c.Parameters.AddWithValue("$f",(object?)scope.Folder?.ToString()??DBNull.Value);
        string folders = scope.Folder.HasValue ? "folderid=$f" : "1";
        string accounts = scope.Account.HasValue ? "accountid=$a" : "1";
        c.CommandText=PreviewDocuments+$", filtered AS (SELECT ({filter}) AS matched,read,size,localid FROM docs WHERE {accounts} AND {folders}) "+
            "SELECT count(*) FILTER(WHERE matched),count(*) FILTER(WHERE matched AND NOT read),coalesce(sum(size) FILTER(WHERE matched),0),coalesce(sum(size) FILTER(WHERE matched AND localid IS NOT NULL),0),count(*) FILTER(WHERE matched IS NULL),count(*) FILTER(WHERE NOT matched) FROM filtered";
        long count,unread,logical,cached,unknown,outside;using(var r=c.ExecuteReader()){r.Read();count=r.GetInt64(0);unread=r.GetInt64(1);logical=r.GetInt64(2);cached=r.GetInt64(3);unknown=r.GetInt64(4);outside=r.GetInt64(5);}
        bool complete=true;long? pending=0;decimal? quota=null;var at=now;string basis="本地邮件库快照";
        c.CommandText="SELECT provider FROM accounts WHERE ($a IS NULL OR id=$a) AND enabled AND NOT removed";
        var providers=new List<string>();using(var r=c.ExecuteReader())while(r.Read())providers.Add(r.GetString(0));
        if(providers.Count==0){complete=false;pending=null;basis="账户缺失或停用";}
        else if(providers.Any(p=>p is not ("local" or "pop3")))
        {
            complete=false;pending=null;basis="远端统计需要已同步的具体目录";
            c.CommandText="""
                SELECT total,quota,sampled,(SELECT count(*) FROM imap_summaries s WHERE s.folder=m.folder AND NOT s.missing)
                FROM imap_measure m JOIN imap_folders f ON f.id=m.folder WHERE f.id=$f AND f.account=$a
                """;
            using var r=c.ExecuteReader();if(r.Read())
            {
                at=DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2));pending=Math.Max(0,r.GetInt64(0)-r.GetInt64(3));quota=r.IsDBNull(1)?null:(decimal)r.GetDouble(1);
                bool fresh=now-at<=TimeSpan.FromMinutes(10);
                complete=fresh&&pending==0;basis=fresh?$"IMAP 快照 · 已缓存 {r.GetInt64(3)}/{r.GetInt64(0)} 个摘要":"服务器快照已超过 10 分钟";
                if(!fresh){pending=null;quota=null;}
            }
        }
        if(unavailable.Count>0){complete=false;basis="缺少扩展 "+string.Join("、",unavailable);}
        return new(count,unread,logical,cached,unknown,outside,complete,pending,quota,at,basis);
    }
}
