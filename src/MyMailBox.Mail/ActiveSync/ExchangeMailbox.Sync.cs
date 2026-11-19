using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using static MyMailBox.Mail.ActiveSync.EasNode;

namespace MyMailBox.Mail.ActiveSync;

public sealed partial class ExchangeMailbox
{
    public Task<MailboxFolder[]> Folders(MailboxIdentity a,CancellationToken t) => Locked(a.Id, () => RefreshHierarchy(a.Id, t), t);

    private async Task<MailboxFolder[]> RefreshHierarchy(Guid account, CancellationToken t)
    {
        using var db=_store.Open();
        using var c=Command(db,account,"SELECT folder_key FROM eas_settings WHERE account=$a");
        string key=c.ExecuteScalar() as string ?? "0";
        var response=await Hierarchy(key);
        if(response.Value(Tag.FolderStatus)=="9" && key!="0"){key="0";response=await Hierarchy(key);}
        Status(response,Tag.FolderStatus);
        using var tx=db.BeginTransaction();c.Transaction=tx;
        if(key=="0"){c.CommandText="DELETE FROM eas_folder WHERE account=$a";c.ExecuteNonQuery();}
        foreach(var change in response.Child(Tag.FolderChanges)?.Children ?? [])
        {
            if(change.Tag is not (Tag.FolderAdd or Tag.FolderUpdate or Tag.FolderDelete))continue;
            string id=change.Value(Tag.FolderId);
            using var cmd=Command(db,account,"DELETE FROM eas_folder WHERE account=$a AND id=$id",("id",id));cmd.Transaction=tx;
            if(change.Tag!=Tag.FolderDelete)
            {
                cmd.CommandText="""
                    INSERT INTO eas_folder VALUES($a,$id,$parent,$name,$type) ON CONFLICT(account,id)
                    DO UPDATE SET parent=$parent,name=$name,kind=$type
                    """;
                cmd.Parameters.AddWithValue("$parent",change.Value(Tag.ParentId));cmd.Parameters.AddWithValue("$name",change.Value(Tag.FolderName));
                cmd.Parameters.AddWithValue("$type",int.Parse(change.Value(Tag.FolderType)));
            }
            cmd.ExecuteNonQuery();
        }
        c.CommandText="UPDATE eas_settings SET folder_key=$key WHERE account=$a";c.Parameters.AddWithValue("$key",response.Value(Tag.FolderKey));c.ExecuteNonQuery();tx.Commit();
        c.Transaction=null;c.CommandText="SELECT id,parent,name,kind FROM eas_folder WHERE account=$a ORDER BY name,id";
        using var r=c.ExecuteReader();var folders=new Dictionary<string,(string Parent,string Name,int Kind)>();
        while(r.Read())folders.Add(r.GetString(0),(r.GetString(1),r.GetString(2),r.GetInt32(3)));
        var list=new List<MailboxFolder>();
        foreach(var (id,entry) in folders)
        {
            if(entry.Kind is not (1 or 2 or 3 or 4 or 5 or 6 or 12))continue;
            var names=new Stack<string>();names.Push(entry.Name);var parent=entry.Parent;var visited=new HashSet<string>{id};
            while(folders.TryGetValue(parent,out var ancestor) && visited.Add(parent)){names.Push(ancestor.Name);parent=ancestor.Parent;}
            list.Add(new(id,string.Join(" / ",names),entry.Kind switch{2=>"inbox",3=>"drafts",4=>"trash",5=>"sent",6=>"outbox",_=>"custom"}));
        }
        return list.ToArray();
        async Task<EasNode> Hierarchy(string cursor)=>await Post(account,"FolderSync",Element(Tag.FolderSync,Value(Tag.FolderKey,cursor)),t)
            ?? throw new InvalidDataException("Exchange 目录响应为空。");
    }

    private void CreateStaging()
    {
        using var db=_store.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS eas_build(account TEXT,folder TEXT,key TEXT NOT NULL,PRIMARY KEY(account,folder));
            CREATE TABLE IF NOT EXISTS eas_stage(account TEXT,folder TEXT,id TEXT,subject TEXT,sender TEXT,stamp TEXT,seen INTEGER NOT NULL,PRIMARY KEY(account,folder,id));
            """;
        cmd.ExecuteNonQuery();
    }
    private static EasNode CollectionRequest(string folder,string key,params EasNode[] options)=>
        Element(Tag.Sync,Element(Tag.Collections,Element(Tag.Collection,[Value(Tag.SyncKey,key),Value(Tag.CollectionId,folder),..options])));
    private async Task Pull(Guid id,string folder,CancellationToken t)
    {
        using var db=_store.Open();using var c=Command(db,id,"SELECT 1 FROM eas_folder WHERE account=$a AND id=$f",("f",folder));
        if(c.ExecuteScalar()==null)throw new InvalidDataException("Exchange 目录已移除，请刷新目录。");
        c.CommandText="SELECT key FROM eas_build WHERE account=$a AND folder=$f";string? key=c.ExecuteScalar() as string;
        bool building=key!=null;
        if(key==null){c.CommandText="SELECT key FROM eas_sync WHERE account=$a AND folder=$f";key=c.ExecuteScalar() as string;building=key==null;}
        key??="0";bool reset=false;
        bool hierarchyRefreshed = false;
        while(true)
        {
            t.ThrowIfCancellationRequested();
            var request=key=="0"?CollectionRequest(folder,key):CollectionRequest(folder,key,Element(Tag.GetChanges),Value(Tag.WindowSize,"100"),
                Element(Tag.Options,Element(Tag.BodyPreference,Value(Tag.BodyType,"1"),Value(Tag.TruncationSize,"0"))));
            var root=await Post(id,"Sync",request,t);
            if(root==null && !building)return;
            var col=root?.Child(Tag.Collections)?.Child(Tag.Collection);
            var status = col?.Child(Tag.Status)?.Text ?? root?.Child(Tag.Status)?.Text;
            if (status == "12" && !hierarchyRefreshed)
            {
                var folders = await RefreshHierarchy(id, t);
                if (!folders.Any(f => f.Id == folder)) throw new InvalidDataException("Exchange 目录已移除，请重新选择目录。");
                hierarchyRefreshed = true;
                continue;
            }
            if(col==null && root?.Child(Tag.Status)!=null)Status(root,Tag.Status);
            if(col==null && (root!=null || !building || key=="0"))throw new InvalidDataException("Exchange 同步响应缺少集合。");
            if(col?.Child(Tag.CollectionId) is {} returned && returned.Text!=folder)throw new InvalidDataException("Exchange 返回了其他目录。");
            if(col?.Value(Tag.Status)=="3" && !reset && key!="0")
            {
                using var tx=db.BeginTransaction();c.Transaction=tx;
                c.CommandText="""
                    DELETE FROM eas_stage WHERE account=$a AND folder=$f;
                    INSERT OR REPLACE INTO eas_build VALUES($a,$f,'0')
                    """;
                c.ExecuteNonQuery();tx.Commit();c.Transaction=null;key="0";building=true;reset=true;continue;
            }
            if(col!=null)Status(col,Tag.Status);
            string next=col?.Value(Tag.SyncKey) ?? key;
            bool more=key=="0" || col?.Child(Tag.MoreAvailable)!=null;
            if(string.IsNullOrEmpty(next) || more && next==key)throw new InvalidDataException("Exchange 没有返回可继续使用的同步键。");
            using(var tx=db.BeginTransaction())
            {
                using var cmd=Command(db,id,"",("f",folder),("key",next));cmd.Transaction=tx;
                foreach(var change in col?.Child(Tag.Commands)?.Children ?? []) ApplyItem(db,tx,id,folder,change,building);
                if(building && !more) FinishBuild(cmd);
                cmd.CommandText=building && more?"INSERT OR REPLACE INTO eas_build VALUES($a,$f,$key)":"INSERT OR REPLACE INTO eas_sync VALUES($a,$f,$key)";
                cmd.ExecuteNonQuery();t.ThrowIfCancellationRequested();tx.Commit();
            }
            if(!more)return;
            key=next;
        }
    }
    private static void ApplyItem(SqliteConnection db,SqliteTransaction tx,Guid account,string folder,EasNode change,bool building)
    {
        if(change.Tag is not (Tag.Add or Tag.Change or Tag.Delete or Tag.SoftDelete))return;
        var id=change.Value(Tag.ServerId);
        var table=building?"eas_stage":"eas_item";
        bool flagsOnly = false;
        using var c=Command(db,account,"",("id",id),("f",folder));c.Transaction=tx;
        if(change.Tag is Tag.Delete or Tag.SoftDelete)
            c.CommandText=$"DELETE FROM {table} WHERE account=$a AND folder=$f AND id=$id";
        else
        {
            var fields=change.Need(Tag.ApplicationData);
            flagsOnly = change.Tag == Tag.Change && fields.Children.Count > 0 && fields.Children.All(node => node.Tag == Tag.Read);
            c.Parameters.AddWithValue("$subject",(object?)fields.Child(Tag.Subject)?.Text??DBNull.Value);
            c.Parameters.AddWithValue("$sender",(object?)fields.Child(Tag.From)?.Text??DBNull.Value);
            c.Parameters.AddWithValue("$date",(object?)fields.Child(Tag.DateReceived)?.Text??DBNull.Value);
            var read=fields.Child(Tag.Read);c.Parameters.AddWithValue("$read",read==null?DBNull.Value:read.Text=="1"?1:0);
            c.CommandText=$"""
                INSERT INTO {table}(account,folder,id,subject,sender,stamp,seen) VALUES($a,$f,$id,coalesce($subject,''),coalesce($sender,''),$date,coalesce($read,0))
                ON CONFLICT(account,folder,id) DO UPDATE SET subject=coalesce($subject,subject),sender=coalesce($sender,sender),stamp=coalesce($date,stamp),seen=coalesce($read,seen)
                """;
            if (!building && !flagsOnly) c.CommandText += ",raw=NULL";
        }
        c.ExecuteNonQuery();
        if (!building)
        {
            c.CommandText = "INSERT INTO eas_event(account,folder,item,flags_only) VALUES($a,$f,$id,$flags)";
            c.Parameters.AddWithValue("$flags", flagsOnly);
            c.ExecuteNonQuery();
        }
    }
    private static void FinishBuild(SqliteCommand c)
    {
        c.CommandText="""
            INSERT INTO eas_event(account,folder,item) SELECT $a,$f,id FROM eas_item WHERE account=$a AND folder=$f
                AND id NOT IN (SELECT id FROM eas_stage WHERE account=$a AND folder=$f);
            DELETE FROM eas_item WHERE account=$a AND folder=$f AND id NOT IN (SELECT id FROM eas_stage WHERE account=$a AND folder=$f);
            INSERT INTO eas_event(account,folder,item) SELECT $a,$f,id FROM eas_stage WHERE account=$a AND folder=$f;
            INSERT INTO eas_item(account,folder,id,subject,sender,stamp,seen) SELECT account,folder,id,subject,sender,stamp,seen FROM eas_stage WHERE account=$a AND folder=$f
                ON CONFLICT(account,folder,id) DO UPDATE SET subject=excluded.subject,sender=excluded.sender,stamp=excluded.stamp,seen=excluded.seen,
                    raw=CASE WHEN subject IS excluded.subject AND sender IS excluded.sender AND stamp IS excluded.stamp THEN raw END;
            DELETE FROM eas_stage WHERE account=$a AND folder=$f; DELETE FROM eas_build WHERE account=$a AND folder=$f
            """;
        c.ExecuteNonQuery();
    }
    private static MailboxItem Item(SqliteDataReader r,int index=0)=>new(r.GetString(index),r.GetString(index+1),r.GetString(index+2),
        !r.IsDBNull(index+3) && DateTimeOffset.TryParse(r.GetString(index+3),out var date)?date:null,r.GetBoolean(index+4));
    public Task<MailboxPageResult> List(MailboxIdentity a,string folder,string? cursor,int limit,CancellationToken t)=>Locked(a.Id,async()=>
    {
        if(limit is <1 or >200)throw new ArgumentOutOfRangeException(nameof(limit));
        int offset=MailboxPaging.Offset(cursor);
        if(offset==0)await Pull(a.Id,folder,t);
        using var db=_store.Open();using var c=Command(db,a.Id,"SELECT id,subject,sender,stamp,seen FROM eas_item WHERE account=$a AND folder=$f ORDER BY stamp DESC,id LIMIT $n OFFSET $offset",
            ("f",folder),("n",limit+1),("offset",offset));
        using var r=c.ExecuteReader();var rows=new List<MailboxItem>();while(r.Read())rows.Add(Item(r));
        return new MailboxPageResult(rows.Take(limit).ToArray(),rows.Count>limit?(offset+limit).ToString():null);
    },t);
    public Task<MailboxDelta> Delta(MailboxIdentity a,string folder,string? cursor,int limit,CancellationToken t)=>Locked(a.Id,async()=>
    {
        if(limit is <1 or >200)throw new ArgumentOutOfRangeException(nameof(limit));
        var start=cursor==null?0:long.TryParse(cursor,out var n) && n>=0?n:throw new InvalidDataException("目录增量游标无效。");
        using var db=_store.Open();using var c=Command(db,a.Id,"SELECT EXISTS(SELECT 1 FROM eas_event WHERE account=$a AND folder=$f AND seq>$start)",("f",folder),("start",start),("n",limit+1));
        if((long)c.ExecuteScalar()! == 0)await Pull(a.Id,folder,t);
        c.CommandText="""
            SELECT e.seq,e.item,i.id,i.subject,i.sender,i.stamp,i.seen,e.flags_only FROM eas_event e LEFT JOIN eas_item i ON i.account=e.account AND i.folder=e.folder AND i.id=e.item
            WHERE e.account=$a AND e.folder=$f AND e.seq>$start ORDER BY e.seq LIMIT $n
            """;
        using var r=c.ExecuteReader();var changed=new Dictionary<string,MailboxItem>();var removed=new HashSet<string>();long last=start;int count=0;
        while (r.Read() && ++count <= limit)
        {
            last = r.GetInt64(0);
            var id = r.GetString(1);
            if (r.IsDBNull(2)) removed.Add(id);
            else
            {
                bool same = r.GetBoolean(7) && (!changed.TryGetValue(id, out var old) || old.OriginalUnchanged);
                changed[id] = Item(r, 2) with { OriginalUnchanged = same };
            }
        }
        return new MailboxDelta(changed.Values.ToArray(),removed.ToArray(),last.ToString(),count>limit);
    },t);
}
