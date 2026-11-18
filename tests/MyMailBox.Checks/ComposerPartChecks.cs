using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static partial class ComposerChecks
{
    private static readonly byte[] Pixel=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jju4AAAAASUVORK5CYII=");
    private static async Task<(SqliteMailStore Store,ProtocolMailService Service,MailDraftStore Drafts,MailDraft Draft)> FilesFixture()
    {
        var store=new SqliteMailStore(Path.Combine(PluginChecks.Repository,"artifacts/checks/parts-"+Guid.NewGuid().ToString("N")));
        var service=new ProtocolMailService(store);var account=await new AccountService(store).Save(new(null,"A","imap-smtp","alice@localhost"));var drafts=new MailDraftStore(store);
        var d=MailDraft.New(account.Id,account.Address);var f=await drafts.AddPart(d.Id,account.Id,"报价.txt",new MemoryStream("attachment"u8.ToArray()),false);
        var img=await drafts.AddPart(d.Id,account.Id,"像素.png",new MemoryStream(Pixel),true);
        d=drafts.Save(d with{Parts=[f,img],Subject="compose-"+Guid.NewGuid().ToString("N"),To="bob@localhost",Body=new("hello","<b>hello</b><img src=\"cid:"+img.Cid+"\" alt=\"像素\">","hello",true)});
        return(store,service,drafts,d);
    }
    public static async Task Parts()
    {
        var f=await FilesFixture();var d=f.Draft;
        MailChecks.Check((await f.Drafts.AddPart(d.Id,d.Account,"again.png",new MemoryStream(Pixel),true)).Id==d.Parts[1].Id,"Duplicate part added");
        f.Drafts.Configure(d.Account,new("署名",["alias@example.test"],100));MailChecks.Check(f.Drafts.Profile(Guid.NewGuid()).Signature=="","Shared signature across accounts");
        await PluginChecks.Reject(()=>f.Service.Enqueue(d.Id,d.Account,d.Message()),"上限");
        f.Drafts.Configure(d.Account,new("署名",["alias@example.test"]));
        await PluginChecks.Reject(()=>f.Service.Enqueue(d.Id,d.Account,d.Message() with{FromIdentity="unbound@example.test"}),"身份");
        await PluginChecks.Reject(()=>f.Service.Enqueue(d.Id,d.Account,d.Message() with{PartIds=[Guid.NewGuid()]}),"附件缺失");
        using var stop=new CancellationTokenSource();stop.Cancel();
        try{await f.Drafts.AddPart(d.Id,d.Account,"cancel",new MemoryStream(Pixel),false,stop.Token);throw new Exception("Canceled upload survived");}catch(OperationCanceledException){}
        await f.Service.Enqueue(d.Id,d.Account,d.Message() with{FromIdentity="alias@example.test"});
        using var db=f.Store.Open();using var c=db.CreateCommand();c.CommandText="SELECT raw FROM outbox WHERE id=$id";c.Parameters.AddWithValue("$id",d.Id.ToString());
        using var mime=MimeMessage.Load(new MemoryStream((byte[])c.ExecuteScalar()!));
        MailChecks.Check(mime.From.Mailboxes.Single().Address=="alias@example.test"&&mime.HtmlBody?.Contains("cid:"+d.Parts[1].Cid)==true&&mime.BodyParts.OfType<MimePart>().Any(p=>p.ContentId==d.Parts[1].Cid)&&mime.Attachments.Single().ContentDisposition?.FileName=="报价.txt","MIME attachments/identity changed");
        Console.WriteLine("PASS attachments/CID snapshot, scoped signatures and approved identities, encoded-size limit, missing/duplicate/cancel before enqueue");
    }
    public static async Task PartsLive()
    {
        var f=await FilesFixture();var d=f.Draft;
        string pin=File.ReadAllText(Path.Combine(PluginChecks.Repository,"artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        var endpoint=new MailEndpoint("127.0.0.1",3465,"alice@localhost",CertificateSha256:pin);
        await f.Service.Save(d.Account,new(endpoint with{Port=3993},endpoint),"local-alice","local-alice");await f.Service.Enqueue(d.Id,d.Account,d.Message());
        var result=await f.Service.Deliver(d.Id);MailChecks.Check(result.State=="sent","SMTP MIME was not accepted: "+result.Message);
        using var reader=new ImapClient();reader.ServerCertificateValidationCallback=(_,cert,_,_)=>cert?.GetCertHashString(HashAlgorithmName.SHA256)==pin;
        await reader.ConnectAsync("127.0.0.1",3993,SecureSocketOptions.SslOnConnect);await reader.AuthenticateAsync("bob@localhost","local-bob");
        await reader.Inbox.OpenAsync(FolderAccess.ReadOnly);var ids=await reader.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(d.Subject));
        using var mail=await reader.Inbox.GetMessageAsync(ids.Single());using var data=new MemoryStream();
        mail.BodyParts.OfType<MimePart>().Single(p=>p.ContentId==d.Parts[1].Cid).Content!.DecodeTo(data);
        MailChecks.Check(mail.TextBody=="hello"&&mail.HtmlBody?.Contains("cid:"+d.Parts[1].Cid)==true&&Pixel.SequenceEqual(data.ToArray())&&mail.Attachments.Count()==1,"Receiving client lost multipart content");
        await reader.DisconnectAsync(true);Console.WriteLine("PASS independent IMAP recipient read exact CID image, attachment, HTML and plain alternatives after real SMTP delivery");
    }
}
