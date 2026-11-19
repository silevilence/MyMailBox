using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailDraftStore
{
    private static (Guid Account,Guid? Id) Source(SqliteMailStore store,string message)
    {
        using var db=store.Open();using var c=db.CreateCommand();
        if (message.StartsWith("graph/", StringComparison.Ordinal)) return GraphReceiveProvider.ReplySource(db, message);
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
        if(c.ExecuteScalar() is string account) return(Guid.Parse(account),Guid.Parse(item));
        c.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name='provider_inbox'";
        if(c.ExecuteScalar()!=null)
        {
            c.CommandText="SELECT account FROM provider_inbox WHERE key=$key";c.Parameters.AddWithValue("$key",message);
            if(c.ExecuteScalar() is string owner)return(Guid.Parse(owner),null);
        }
        throw new InvalidDataException("原邮件已移除。");
    }
    public Guid SourceAccount(string message)=>Source(_store,message).Account;
    internal static async Task<byte[]> CachedOriginal(SqliteMailStore store,string key,Guid? account,CancellationToken token)
    {
        var source=Source(store,key);
        if (account.HasValue && source.Account!=account) throw new InvalidDataException("这封邮件不在选中的账户中。");
        if(source.Id is {} local)return await store.Read(local,token);
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT raw FROM provider_inbox WHERE key=$key AND account=$account";
        c.Parameters.AddWithValue("$key",key);c.Parameters.AddWithValue("$account",source.Account.ToString());
        return c.ExecuteScalar() as byte[] ?? throw new InvalidDataException("请先下载邮件。");
    }
    public async Task<MailDraft> Respond(ReplyIntent intent,Guid account,CancellationToken token=default)
    {
        byte[] raw;
        if(intent.ReadAccount!=null && intent.ReadMime is {Length:>0 and <=MimeReader.MaxMessageBytes} readMime)
            raw=readMime;
        else
        {
            raw=await CachedOriginal(_store,intent.Key,null,token);
        }
        var accounts=await new AccountService(_store).List(token);
        var sending=accounts.FirstOrDefault(a=>a.Id==account && a.Enabled && a.Capabilities.HasFlag(AccountCapabilities.Send)) ??
            throw new InvalidDataException("请选择可发送邮件的账户。");
        var aliases=Profile(account).Aliases.Prepend(sending.Address).ToArray();
        var own=accounts.SelectMany(a=>Profile(a.Id).Aliases.Prepend(a.Address)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var original=await MimeMessage.LoadAsync(new MemoryStream(raw,false),token);
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
