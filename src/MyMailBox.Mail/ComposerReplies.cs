using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailDraftStore
{
    private (Guid Account,Guid Id) Source(string message)
    {
        using var db=_store.Open();using var c=db.CreateCommand();
        string item;
        if(Guid.TryParse(message,out var local))item=local.ToString();
        else
        {
            var split=message.Split('/');
            if(split.Length!=2 || !Guid.TryParse(split[0],out var folder) || !uint.TryParse(split[1],out var uid))
                throw new InvalidDataException("原邮件引用已失效。");
            c.CommandText="SELECT message FROM imap_summaries WHERE folder=$f AND uid=$u";c.Parameters.AddWithValue("$f",folder.ToString());c.Parameters.AddWithValue("$u",(long)uid);
            item=c.ExecuteScalar() as string ?? throw new InvalidDataException("请先下载并打开原邮件。");
        }
        c.CommandText="SELECT f.account_id FROM messages m JOIN folders f ON f.id=m.folder_id WHERE m.id=$id";c.Parameters.AddWithValue("$id",item);
        return(c.ExecuteScalar() is string account?Guid.Parse(account):throw new InvalidDataException("原邮件已移除。"),Guid.Parse(item));
    }
    public Guid SourceAccount(string message)=>Source(message).Account;
    public async Task<MailDraft> Respond(ReplyIntent intent,Guid account,CancellationToken token=default)
    {
        var source=Source(intent.Key);
        var accounts=await new AccountService(_store).List(token);
        var sending=accounts.FirstOrDefault(a=>a.Id==account && a.Enabled && a.Capabilities.HasFlag(AccountCapabilities.Send)) ??
            throw new InvalidDataException("请选择可发送邮件的账户。");
        var aliases=Profile(account).Aliases.Prepend(sending.Address).ToArray();
        var own=accounts.SelectMany(a=>Profile(a.Id).Aliases.Prepend(a.Address)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var original=await MimeMessage.LoadAsync(new MemoryStream(await _store.Read(source.Id,token)),token);
        var visible=original.To.Mailboxes.Concat(original.Cc.Mailboxes).ToArray();
        var identity=aliases.FirstOrDefault(a=>visible.Any(m=>m.Address.Equals(a,StringComparison.OrdinalIgnoreCase))) ?? sending.Address;
        var receivers=new HashSet<string>(own,StringComparer.OrdinalIgnoreCase);var to=new InternetAddressList();var cc=new InternetAddressList();
        void Add(InternetAddressList dest,IEnumerable<MailboxAddress> items){foreach(var m in items)if(receivers.Add(m.Address))dest.Add(m);}
        bool forward=intent.Mode==ReplyMode.Forward;
        if(!forward)
        {
            var reply=original.ReplyTo.Mailboxes.ToArray();
            Add(to,reply.Length>0?reply:original.From.Mailboxes);
            if(to.Count==0 && original.From.Mailboxes.Any(m=>own.Contains(m.Address)))Add(to,original.To.Mailboxes);
            if(intent.Mode==ReplyMode.ReplyAll){Add(to,original.To.Mailboxes);Add(cc,original.Cc.Mailboxes);}
        }
        string prefix=forward?"Fwd:":"Re:",subject=original.Subject ?? "";
        if(!subject.TrimStart().StartsWith(prefix,StringComparison.OrdinalIgnoreCase))subject=prefix+" "+subject;
        var parent=forward?null:original.MessageId;
        var references=forward?[]:original.References.Concat(parent is null?[]:new[]{parent}).Distinct(StringComparer.Ordinal).ToArray();
        string text=original.TextBody ?? "";
        if(text.Length==0 && original.HtmlBody is {} html)
        {
            using var parsed=new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);foreach(var active in parsed.QuerySelectorAll("script,style,head"))active.Remove();
            text=parsed.Body?.TextContent ?? "";
        }
        var draft=MailDraft.New(account,identity) with{To=to.ToString(),Cc=cc.ToString(),Subject=subject,InReplyTo=parent,References=references,
            Quote=new($"{original.From} · {original.Date:g} · {original.Subject}",text)};
        if(forward)
        {
            var files=new List<ComposerPart>();foreach(var part in original.Attachments)
            {
                token.ThrowIfCancellationRequested();using var data=new MemoryStream();string? name;
                if(part is MimePart mime){await mime.Content!.DecodeToAsync(data,token);name=mime.FileName;}
                else if(part is MessagePart attached && attached.Message is {} nested){await nested.WriteToAsync(data,token);name=part.ContentDisposition?.FileName ?? "附带邮件.eml";}
                else continue;
                data.Position=0;var file=await AddPart(draft.Id,account,name ?? "附件",data,false,token);if(files.All(p=>p.Id!=file.Id))files.Add(file);
            }
            draft=draft with{Parts=files.ToArray()};
        }
        return Save(draft);
    }
}
