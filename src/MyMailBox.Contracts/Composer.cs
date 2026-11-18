namespace MyMailBox.Contracts;

public sealed record DraftBody(string Plain="",string Html="",string RichText="",bool Rich=false);
public enum ReplyMode { Reply, ReplyAll, Forward }
public sealed record ReplyIntent(string Key,ReplyMode Mode);
public sealed record MailQuote(string Heading,string Text,bool Included=true);
public sealed record MailDraft(Guid Id,int Version,Guid Account,string Identity,string To,string Cc,string Bcc,string Subject,DraftBody Body)
{
    public string? InReplyTo { get; init; }
    public string[] References { get; init; } = [];
    public MailQuote? Quote { get; init; }
    public ComposerPart[] Parts { get; init; } = [];
    public static MailDraft New(Guid account,string identity)=>new(Guid.NewGuid(),0,account,identity,"","","","",new());
    public TextMail Message()
    {
        var text=Body.Plain;string? html=Body.Rich ? Body.Html : null;
        if (Quote is {Included:true} q)
        {
            text+="\n\n"+q.Heading+"\n"+string.Join("\n",q.Text.Replace("\r\n","\n").Split('\n').Select(x=>"> "+x));
            if(html is not null)html+="<blockquote>"+System.Net.WebUtility.HtmlEncode(q.Heading+"\n"+q.Text).Replace("\n","<br>")+"</blockquote>";
        }
        return new(To,Cc,Bcc,Subject,text){Html=html,FromIdentity=Identity,PartIds=Parts.Select(p=>p.Id).ToArray(),InReplyTo=InReplyTo,References=References};
    }
}
public sealed record ComposerPart(Guid Id,string Name,string Mime,long Size,string? Cid);
public sealed record SendingProfile(string Signature,string[] Aliases,long MaxBytes=25L*1024*1024);
public interface IMailDrafts
{
    Guid SourceAccount(string message);
    Task<MailDraft> Respond(ReplyIntent intent,Guid account,CancellationToken token=default);
    SendingProfile Profile(Guid account);
    void Configure(Guid account,SendingProfile profile);
    Task<ComposerPart> AddPart(Guid draft,Guid account,string name,Stream data,bool inline,CancellationToken token=default);
    byte[] PartData(Guid account,Guid id);
    IReadOnlyList<MailDraft> List(Guid account);
    MailDraft Save(MailDraft draft);
    void Submitted(Guid draft,int version);
}
public sealed record ComposerPage(IAccountService Accounts,IMailDrafts Drafts,IOutboxService Outbox);
public sealed record EditorTemplate(string Name,string Text);
public sealed record ComposerEditor(string Id,string Name,bool SupportsRich,IReadOnlyList<EditorTemplate> Templates,
    Func<DraftBody,DraftBody> Open,Func<string,CancellationToken,Task<string>> Spell)
{
    public Func<Guid,string?>? Signature {get;init;}
}
