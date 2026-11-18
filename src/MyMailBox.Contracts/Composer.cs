namespace MyMailBox.Contracts;

public sealed record DraftBody(string Plain="",string Html="",string RichText="",bool Rich=false);
public sealed record MailDraft(Guid Id,int Version,Guid Account,string Identity,string To,string Cc,string Bcc,string Subject,DraftBody Body)
{
    public static MailDraft New(Guid account,string identity)=>new(Guid.NewGuid(),0,account,identity,"","","","",new());
    public TextMail Message()=>new(To,Cc,Bcc,Subject,Body.Plain){Html=Body.Rich ? Body.Html : null};
}
public interface IMailDrafts
{
    IReadOnlyList<MailDraft> List(Guid account);
    MailDraft Save(MailDraft draft);
    void Submitted(Guid draft,int version);
}
public sealed record ComposerPage(IAccountService Accounts,IMailDrafts Drafts,IOutboxService Outbox);
public sealed record EditorTemplate(string Name,string Text);
public sealed record ComposerEditor(string Id,string Name,bool SupportsRich,IReadOnlyList<EditorTemplate> Templates,
    Func<DraftBody,DraftBody> Open,Func<string,CancellationToken,Task<string>> Spell);
