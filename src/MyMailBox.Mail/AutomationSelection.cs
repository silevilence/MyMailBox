using MailKit;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyMailBox.Mail;

public sealed partial class MailAutomationService
{
    public async Task<TargetSnapshot> Select(AutomationPlan plan,DateTimeOffset? at=null,CancellationToken token=default)
    {
        var library=await Library(token);
        return await Task.Run(()=>_search.SelectTargets(plan,library,at??_clock.GetUtcNow(),null,token),token);
    }
    public async Task<string?> Recheck(AutomationPlan plan,TargetSnapshot snapshot,TargetMail mail,CancellationToken token=default)
    {
        if(!snapshot.Items.Any(i=>i.Id==mail.Id && i.Stamp==mail.Stamp))return "邮件不在本次快照中。";
        if(mail.Validity.HasValue)
        {
            if(!await _protocols.RefreshTarget(mail,token))return "服务器邮件已移动、删除或更换标识。";
        }
        var library=await Library(token);
        var check=await Task.Run(()=>_search.SelectTargets(plan,library,snapshot.At,mail.Id,token),token);
        var actual=check.Items.SingleOrDefault();
        if(actual is null)return "邮件已受保护、不再满足条件或已达到低水位。";
        if(actual.Folder!=mail.Folder||actual.Validity!=mail.Validity||actual.Stamp!=mail.Stamp||mail.Hash is not null&&actual.Hash!=mail.Hash)
            return "来源内容或状态已改变，请重新选择。";
        return null;
    }
}

public sealed partial class ProtocolMailService
{
    internal Task<bool> RefreshTarget(TargetMail mail,CancellationToken token)=>OnImap(mail.Account,async(client,cache)=>
    {
        var remote=await VerifiedFolder(client,cache,mail.Folder,MailKit.FolderAccess.ReadOnly,token);
        if(remote.UidValidity!=mail.Validity)return false;
        var uid=new MailKit.UniqueId(uint.Parse(mail.Key,System.Globalization.CultureInfo.InvariantCulture));
        var result=await remote.FetchAsync(new[]{uid},MailKit.MessageSummaryItems.UniqueId|MailKit.MessageSummaryItems.Flags,token);
        cache.Snapshot(mail.Folder,[uid],result);return result.Any(s=>s.UniqueId==uid);
    },token);
}

public sealed partial class MailSearchService
{
    internal TargetSnapshot SelectTargets(AutomationPlan plan,SearchLibrary library,DateTimeOffset at,string? only,CancellationToken token)
    {
        var t=plan.Target??throw new InvalidDataException("请设置实际处理范围。");
        string Scope(Guid? a,Guid? f)=>library.Scopes.FirstOrDefault(s=>s.Account==a&&s.Folder==f)?.Name??throw new InvalidDataException("范围已失效，请重新选择。");
        string trigger=Scope(plan.When.Account,plan.When.Folder),target=Scope(t.Account,t.Folder);
        if(t.AgeDays is <0 or >36500||t.MaxBytes<0||t.MaxCount is <1 or >10000||t.KeepNewest<0||t.LowWaterBytes<0||t.Order is not (SearchOrder.Oldest or SearchOrder.Largest or SearchOrder.Newest))
            throw new InvalidDataException("选择器的天数、排序或数量/字节边界无效。");
        if(t.LowWaterBytes.HasValue)
        {
            var volume=CountScope(new(t.Account,t.Folder,"","count>=0",0,0),DateTimeOffset.UtcNow,token);
            if(!volume.Complete || t.Descendants && volume.Basis.StartsWith("IMAP",StringComparison.Ordinal))throw new InvalidDataException("低水位需要完整且当前的范围统计，请先同步目录。");
        }
        using var db=_store.Open(true);PreparePreview(db,token);using var c=db.CreateCommand();
        var parts=new List<ConditionNode>();if(!string.IsNullOrWhiteSpace(t.Filter))parts.Add(ConditionTree.Parse(t.Filter).Root);
        if(t.SavedSearch is Guid saved)
        {
            var def=library.Saved.FirstOrDefault(x=>x.Id==saved);if(def is null||!def.HasScope(library))throw new InvalidDataException("保存搜索缺失或范围已失效。");
            parts.Add(def.Condition().Root);
        }
        var unavailable=new HashSet<string>();
        string filter=parts.Count==0?"1":Compile(db,c,new(ConditionTree.CurrentVersion,parts.Count==1?parts[0]:new(ConditionLogic.And,null,parts.ToArray())),unavailable,token,preview:true);
        if(unavailable.Count>0)throw new InvalidDataException("目标条件缺少能力："+string.Join("、",unavailable));
        c.Parameters.AddWithValue("$a",(object?)t.Account?.ToString()??DBNull.Value);c.Parameters.AddWithValue("$f",(object?)t.Folder?.ToString()??DBNull.Value);c.Parameters.AddWithValue("$sub",t.Descendants);
        string scoped=PreviewDocuments+"""
            , tree(id) AS (SELECT id FROM folders WHERE id=$f UNION SELECT f.id FROM folders f JOIN tree t ON f.parent_id=t.id WHERE $sub),
            scoped AS (SELECT docs.* FROM docs WHERE ($a IS NULL OR accountid=$a) AND ($f IS NULL OR folderid IN tree OR ($sub AND folderid IN
                (SELECT child.id FROM imap_folders parent JOIN imap_delimiter d ON d.folder=parent.id JOIN imap_folders child
                ON child.account=parent.account AND child.source=parent.source AND substr(child.path,1,length(parent.path||d.separator))=parent.path||d.separator WHERE parent.id=$f))))
            """;
        c.CommandText=scoped+" SELECT coalesce(sum(size),0) FROM scoped";long scopeBytes=(long)c.ExecuteScalar()!;
        var targets=new List<TargetMail>();long selected=0;
        if(!t.LowWaterBytes.HasValue||scopeBytes>t.LowWaterBytes)
        {
            c.Parameters.AddWithValue("$keep",t.KeepNewest);c.Parameters.AddWithValue("$star",t.KeepStarred);c.Parameters.AddWithValue("$label",t.KeepLabel);
            c.Parameters.AddWithValue("$age",t.AgeDays);c.Parameters.AddWithValue("$cut",at.AddDays(-t.AgeDays).ToUnixTimeMilliseconds());
            c.Parameters.AddWithValue("$fixed",t.Fixed is null?DBNull.Value:JsonSerializer.Serialize(t.Fixed));c.Parameters.AddWithValue("$only",(object?)only??DBNull.Value);
            // Keep the docs alias for correlated extension predicates compiled against a stable hit ID.
            c.CommandText=scoped+"""
                , ranked AS (SELECT scoped.*,row_number() OVER(ORDER BY sent DESC,id) AS recent FROM scoped)
                 SELECT docs.id,accountid,folderid,subject,sent,size,validity,uid,m.sha256,read,star,tag FROM ranked docs
                 LEFT JOIN (SELECT id AS mid,sha256 FROM messages) m ON m.mid=localid
                 WHERE recent>$keep AND (NOT $star OR NOT star) AND ($label='' OR NOT EXISTS(SELECT 1 FROM json_each(tag) WHERE value=$label))
                 AND ($age=0 OR sent<$cut) AND ($fixed IS NULL OR docs.id IN (SELECT value FROM json_each($fixed)))
                 AND ($only IS NULL OR docs.id=$only) AND
                """+" ("+filter+") ORDER BY "+Ordering(t.Order)+",docs.id";
            using var r=c.ExecuteReader();while(r.Read())
            {
                token.ThrowIfCancellationRequested();long size=r.GetInt64(5);
                if(t.MaxBytes>0&&size>t.MaxBytes-selected)continue;
                string id=r.GetString(0),sub=r.GetString(3);
                var stamp=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{sub,size,sent=r.IsDBNull(4)?(long?)null:r.GetInt64(4),read=r.GetBoolean(9),star=r.GetBoolean(10),tag=r.IsDBNull(11)?"":r.GetString(11)})));
                targets.Add(new(id,Guid.Parse(r.GetString(1)),Guid.Parse(r.GetString(2)),r.IsDBNull(7)?id:r.GetInt64(7).ToString(),sub,
                    r.IsDBNull(4)?null:DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),size,r.IsDBNull(6)?null:(uint)r.GetInt64(6),r.IsDBNull(8)?null:r.GetString(8),stamp));
                selected+=size;if(targets.Count>=t.MaxCount || t.LowWaterBytes.HasValue&&scopeBytes-selected<=t.LowWaterBytes)break;
            }
        }
        return new(Guid.NewGuid(),at,trigger,target,targets,scopeBytes,selected,$"以 {at.LocalDateTime:g} 为固定时刻；按当前可见目录和已同步邮件选择，保留最近 {t.KeepNewest} 封。");
    }
}
