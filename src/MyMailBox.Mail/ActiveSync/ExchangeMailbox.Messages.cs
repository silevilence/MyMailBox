using MyMailBox.Contracts;
using static MyMailBox.Mail.ActiveSync.EasNode;

namespace MyMailBox.Mail.ActiveSync;

public sealed partial class ExchangeMailbox
{
    public async Task Read(MailboxIdentity a,string folder,string item,Stream destination,CancellationToken t)
    {
        await Account(a.Id,t);
        using (var local = _store.Open())
        using (var lookup = Command(local,a.Id,"SELECT raw FROM eas_item WHERE account=$a AND folder=$f AND id=$id",("f",folder),("id",item)))
        {
            if (lookup.ExecuteScalar() is byte[] original)
            {
                await destination.WriteAsync(original,t);
                return;
            }
        }
        // A cold read waits for the collection lock and checks the cache again after synchronizing.
        var raw=await Locked(a.Id,async()=>
        {
            await Account(a.Id,t);
            using var db=_store.Open();using var c=Command(db,a.Id,"SELECT raw FROM eas_item WHERE account=$a AND folder=$f AND id=$id",("f",folder),("id",item));
            var cached=c.ExecuteScalar();
            if(cached==null)throw new InvalidDataException("邮件已移出所选目录。");
            if(cached is byte[] bytes)return bytes;
            var node=Element(Tag.ItemOperations,Element(Tag.FetchItem,Value(Tag.Store,"Mailbox"),Value(Tag.CollectionId,folder),Value(Tag.ServerId,item),
                Element(Tag.FetchOptions,Value(Tag.MIMESupport,"2"),Element(Tag.BodyPreference,Value(Tag.BodyType,"4"),Value(Tag.TruncationSize,MimeReader.MaxMessageBytes.ToString())))));
            var root=await Post(a.Id,"ItemOperations",node,t) ?? throw new InvalidDataException("Exchange 原文响应为空。");
            Status(root,Tag.ItemStatus);var fetched=root.Need(Tag.ItemResponse).Need(Tag.FetchItem);Status(fetched,Tag.ItemStatus);
            if(fetched.Value(Tag.CollectionId)!=folder || fetched.Value(Tag.ServerId)!=item)throw new InvalidDataException("Exchange 返回了其他邮件。");
            var body=fetched.Need(Tag.Properties).Need(Tag.Body);
            if(body.Value(Tag.BodyType)!="4" || body.Child(Tag.Truncated)?.Text=="1")throw new InvalidDataException("服务器未返回完整 MIME，不能将此响应保存为原文。");
            var original=body.Need(Tag.BodyData).Data??throw new InvalidDataException("邮件原文为空。");
            await new MimeReader().Parse(original,t);
            c.CommandText="UPDATE eas_item SET raw=$raw WHERE account=$a AND folder=$f AND id=$id";c.Parameters.AddWithValue("$raw",original);c.ExecuteNonQuery();
            return original;
        },t);
        await destination.WriteAsync(raw,t);
    }
    private sealed class Refused(string message):InvalidOperationException(message);
    public async Task<SendOutcome> Send(MailboxIdentity a,Guid operation,Stream mime,string[] envelope,CancellationToken t)
    {
        var settings=await Settings(a.Id,t);
        if(!settings.WriteAllowed || !settings.DeviceAllowed || !settings.Commands.Contains("SendMail"))
            return new("rejected","请先检查 Exchange 能力并允许此账户发信。",[],envelope);
        using var data=new MemoryStream();using(var limited=new AttachmentService.CountingStream(data,MimeReader.MaxMessageBytes,null,"邮件超过 25 MiB。"))
            await mime.CopyToAsync(limited,t);
        var raw=data.ToArray();data.Position=0;
        using(var message=await MimeKit.MimeMessage.LoadAsync(data,t))
        {
            var recipients=message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes).Select(box=>box.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(!recipients.SetEquals(envelope))return new("rejected","邮件地址与 Exchange 投递收件人不一致，请核对后重试。",[],envelope);
        }
        var command=Element(Tag.SendMail,Value(Tag.ClientId,operation.ToString("N")),Element(Tag.SaveInSentItems),Binary(Tag.Mime,raw));
        try
        {
            var response=await Post(a.Id,"SendMail",command,t,true);
            if(response==null)return new("sent","Exchange 已接受邮件，已请求保存服务器已发送副本。",envelope,[]);
            var code=response.Value(Tag.SendStatus);
            if(code is "115" or "119" or "120" or "121" or "126")
                return new("rejected","Exchange 拒绝提交，状态 "+code+"。",[],envelope);
            return new("unknown","Exchange 返回非成功状态，请先核对服务器已发送目录。",[],[]);
        }
        catch(Refused e){return new("rejected",e.Message,[],envelope);}
    }
    public Task Mark(MailboxIdentity a,string folder,string item,bool read,CancellationToken t)=>Locked(a.Id,async()=>
    {
        using var db=_store.Open();using var c=Command(db,a.Id,"SELECT key FROM eas_sync WHERE account=$a AND folder=$f",("f",folder),("id",item));
        var key=c.ExecuteScalar() as string??throw new InvalidDataException("请先同步邮件目录。");
        var command=CollectionRequest(folder,key,Value(Tag.GetChanges,"0"),Element(Tag.Commands,Element(Tag.Change,Value(Tag.ServerId,item),Element(Tag.ApplicationData,Value(Tag.Read,read?"1":"0")))));
        var root=await Post(a.Id,"Sync",command,t,true)??throw new InvalidDataException("Exchange 没有返回标记结果，请刷新核对。");
        var col=root.Need(Tag.Collections).Need(Tag.Collection);Status(col,Tag.Status);
        if(col.Value(Tag.CollectionId)!=folder)throw new InvalidDataException("Exchange 返回了其他目录。");
        foreach(var result in col.Child(Tag.Responses)?.Children??[])Status(result,Tag.Status);
        using var tx=db.BeginTransaction();c.Transaction=tx;
        foreach(var change in col.Child(Tag.Commands)?.Children??[])ApplyItem(db,tx,a.Id,folder,change,false);
        c.CommandText="""
            UPDATE eas_sync SET key=$key WHERE account=$a AND folder=$f;
            UPDATE eas_item SET seen=$read WHERE account=$a AND folder=$f AND id=$id;
            INSERT INTO eas_event(account,folder,item,flags_only) VALUES($a,$f,$id,1)
            """;c.Parameters.AddWithValue("$key",col.Value(Tag.SyncKey));c.Parameters.AddWithValue("$read",read);c.ExecuteNonQuery();tx.Commit();return true;
    },t);
    public Task<string> Move(MailboxIdentity a,string folder,string item,string destination,CancellationToken t)=>Locked(a.Id,async()=>
    {
        using var db=_store.Open();using var c=Command(db,a.Id,"SELECT count(*) FROM eas_folder WHERE account=$a AND id IN ($f,$dest)",("f",folder),("dest",destination),("id",item));
        if((long)c.ExecuteScalar()! !=2)throw new InvalidDataException("请选择本账户的另一个 Exchange 目录。");
        var root=await Post(a.Id,"MoveItems",Element(Tag.MoveItems,Element(Tag.Move,Value(Tag.SrcMsgId,item),Value(Tag.SrcFldId,folder),Value(Tag.DstFldId,destination))),t,true)
            ??throw new InvalidDataException("移动回执为空，请刷新核对目标和来源。");
        var response=root.Need(Tag.MoveResponse);Status(response,Tag.MoveStatus,"3");
        if(response.Value(Tag.SrcMsgId)!=item)throw new InvalidDataException("移动回执与源邮件不匹配。");
        var target=response.Value(Tag.DstMsgId);c.Parameters.AddWithValue("$target",target);
        using var tx=db.BeginTransaction();c.Transaction=tx;
        c.CommandText="""
            INSERT OR REPLACE INTO eas_item SELECT account,$dest,$target,subject,sender,stamp,seen,raw FROM eas_item WHERE account=$a AND folder=$f AND id=$id;
            DELETE FROM eas_item WHERE account=$a AND folder=$f AND id=$id;
            INSERT INTO eas_event(account,folder,item) VALUES($a,$f,$id),($a,$dest,$target)
            """;c.ExecuteNonQuery();tx.Commit();return target;
    },t);
}
