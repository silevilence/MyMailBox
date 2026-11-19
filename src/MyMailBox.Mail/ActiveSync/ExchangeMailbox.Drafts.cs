using MimeKit;
using MyMailBox.Contracts;
using static MyMailBox.Mail.ActiveSync.EasNode;

namespace MyMailBox.Mail.ActiveSync;

public sealed partial class ExchangeMailbox
{
    public async Task<string> SaveDraft(MailDraft draft,CancellationToken t)
    {
        var account=await Account(draft.Account,t);
        var settings=await Settings(account.Id,t);
        if(settings.Version is not ("16.0" or "16.1") || !settings.WriteAllowed || !settings.DeviceAllowed)
            throw new InvalidDataException("服务器草稿需要 ActiveSync 16.0 或更高版本，并允许此账户写入。");
        var drafts=new MailDraftStore(_store);
        var saved=drafts.List(account.Id).SingleOrDefault(d=>d.Id==draft.Id && d.Version==draft.Version)
            ?? throw new InvalidDataException("草稿已变化，请先保存当前本地版本。");
        var folders=await Folders(new(account.Id,account.Address),t);
        var folder=folders.SingleOrDefault(f=>f.Role=="drafts") ?? throw new InvalidDataException("服务器未提供草稿目录。");
        return await Locked(account.Id,async()=>
        {
            using var db=_store.Open();using var cmd=Command(db,account.Id,"SELECT coalesce(remote,'') FROM eas_draft WHERE account=$a AND draft=$id AND version=$v",
                ("id",draft.Id.ToString()),("v",draft.Version),("folder",folder.Id));
            if(cmd.ExecuteScalar() is string receipt)return receipt.Length!=0?receipt:throw new InvalidDataException("此版本已提交但未取得完整回执，请先核对服务器草稿目录。");
            cmd.CommandText="SELECT key FROM eas_sync WHERE account=$a AND folder=$folder";
            if(cmd.ExecuteScalar() is not string)await Pull(account.Id,folder.Id,t);
            string key=(string)cmd.ExecuteScalar()!;
            var (raw,_)=await drafts.Outgoing(saved.Id,account,saved.Message(),null,true,t);
            using var mime=await MimeMessage.LoadAsync(new MemoryStream(raw,false),t);
            var data=Element(Tag.ApplicationData,Value(Tag.Subject,mime.Subject??""),Element(Tag.Body,
                Value(Tag.BodyType,mime.HtmlBody!=null?"2":"1"),Value(Tag.BodyData,mime.HtmlBody??mime.TextBody??"")));
            foreach(var (tag,addresses) in new[]{(Tag.To,mime.To),(Tag.Cc,mime.Cc),(Tag.Bcc,mime.Bcc)})
                if(addresses.Count>0)data.Children.Add(Value(tag,addresses.ToString()));
            var attachments=Element(Tag.Attachments);
            foreach(var part in mime.BodyParts.OfType<MimePart>().Where(p=>p.FileName!=null || p.IsAttachment || p.ContentId!=null))
            {
                using var bytes=new MemoryStream();await part.Content!.DecodeToAsync(bytes,t);
                var attach=Element(Tag.AddAttachment,Value(Tag.AttachmentClientId,Guid.NewGuid().ToString("N")),Value(Tag.Method,"1"),
                    Value(Tag.ContentType,part.ContentType.MimeType),Binary(Tag.Content,bytes.ToArray()),Value(Tag.DisplayName,part.FileName??"attachment"));
                if(part.ContentId!=null){attach.Children.Add(Value(Tag.ContentId,part.ContentId));attach.Children.Add(Element(Tag.IsInline));}
                attachments.Children.Add(attach);
            }
            if(attachments.Children.Count!=0)data.Children.Add(attachments);
            string client=Guid.NewGuid().ToString("N");cmd.Parameters.AddWithValue("$client",client);
            cmd.CommandText="INSERT INTO eas_draft(account,draft,version,client) VALUES($a,$id,$v,$client)";cmd.ExecuteNonQuery();
            var command=CollectionRequest(folder.Id,key,Value(Tag.GetChanges,"0"),Element(Tag.Commands,Element(Tag.Add,Value(Tag.SyncClientId,client),data)));
            EasNode? result;
            try {result=await Post(account.Id,"Sync",command,t,true);}
            catch(Refused)
            {
                cmd.CommandText="DELETE FROM eas_draft WHERE account=$a AND draft=$id AND version=$v";cmd.ExecuteNonQuery();throw;
            }
            var col=result?.Need(Tag.Collections).Need(Tag.Collection)??throw new InvalidDataException("草稿回执为空，请核对服务器。");
            Status(col,Tag.Status);if(col.Value(Tag.CollectionId)!=folder.Id)throw new InvalidDataException("草稿回执目录不匹配。");
            var added=col.Need(Tag.Responses).Need(Tag.Add);Status(added,Tag.Status);
            if(added.Value(Tag.SyncClientId)!=client)throw new InvalidDataException("草稿回执标识不匹配。");
            var id=added.Value(Tag.ServerId);using var tx=db.BeginTransaction();cmd.Transaction=tx;
            foreach(var change in col.Child(Tag.Commands)?.Children??[])ApplyItem(db,tx,account.Id,folder.Id,change,false);
            cmd.CommandText="""
                UPDATE eas_sync SET key=$key WHERE account=$a AND folder=$folder;
                UPDATE eas_draft SET remote=$remote WHERE account=$a AND draft=$id AND version=$v;
                INSERT OR REPLACE INTO eas_item(account,folder,id,subject,sender,seen,raw) VALUES($a,$folder,$remote,$subject,$sender,0,$raw);
                INSERT INTO eas_event(account,folder,item) VALUES($a,$folder,$remote)
                """;
            cmd.Parameters.AddWithValue("$key",col.Value(Tag.SyncKey));cmd.Parameters.AddWithValue("$remote",id);
            cmd.Parameters.AddWithValue("$subject",mime.Subject??"");cmd.Parameters.AddWithValue("$sender",mime.From.ToString());
            cmd.Parameters.AddWithValue("$raw",raw);cmd.ExecuteNonQuery();tx.Commit();return id;
        },t);
    }
}
