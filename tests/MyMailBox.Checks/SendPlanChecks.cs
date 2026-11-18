using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static partial class ComposerChecks
{
    private static void Due(SqliteMailStore store,Guid id,int secondsAgo)
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="UPDATE outbox SET due=unixepoch()-$late WHERE id=$id";
        c.Parameters.AddWithValue("$late",secondsAgo);c.Parameters.AddWithValue("$id",id.ToString());c.ExecuteNonQuery();
    }
    public static async Task SendPlans()
    {
        var f=await FilesFixture();var d=f.Draft;var plan=new SendPlan(DateTimeOffset.UtcNow.AddHours(1),"UTC",MissedSend.HoldForReview,0,d.Version);
        await f.Service.Schedule(d.Id,d.Account,d.Message(),plan);
        var restarted=new ProtocolMailService(f.Store);var stored=await restarted.GetSendPlan(d.Id);
        MailChecks.Check(stored is {Revision:1,Zone:"UTC"} && (await restarted.Deliver(d.Id)).State=="scheduled","Early/restarted delivery bypassed due time");
        await PluginChecks.Reject(()=>restarted.Enqueue(d.Id,d.Account,d.Message()),"预约");
        d=f.Drafts.Save(d with{Subject="编辑后"});Due(f.Store,d.Id,1);MailChecks.Check((await restarted.Deliver(d.Id)).State=="held","Draft changed after scheduling but old content sent");
        await restarted.Schedule(d.Id,d.Account,d.Message(),plan with{Revision=1,DraftVersion=d.Version});
        await PluginChecks.Reject(()=>restarted.Schedule(d.Id,d.Account,d.Message(),plan with{Revision=1,DraftVersion=d.Version}),"刷新");
        Due(f.Store,d.Id,120);MailChecks.Check((await restarted.Deliver(d.Id)).State=="held","Missed activation policy ignored");
        await restarted.Cancel(d.Id);MailChecks.Check(await restarted.GetSendPlan(d.Id) is null && (await restarted.Deliver(d.Id)).State=="canceled" &&
            f.Drafts.List(d.Account).Single().Id==d.Id,"Cancellation after hold lost editable draft");
        await restarted.Enqueue(d.Id,d.Account,d.Message());
        MailChecks.Check((await ((IOutboxService)restarted).List(d.Account,0,20)).Single().State=="pending","Cannot explicitly send canceled scheduled draft");
        Console.WriteLine("PASS scheduled draft restart, early delivery gate, optimistic edits, missed-time hold, cancellation and explicit requeue");
    }
    public static async Task SendPlanLive()
    {
        var f=await FilesFixture();var d=f.Draft;
        var pin=File.ReadAllText(Path.Combine(PluginChecks.Repository,"artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        var endpoint=new MailEndpoint("127.0.0.1",3993,"alice@localhost",CertificateSha256:pin);
        await f.Service.Save(d.Account,new(endpoint,endpoint with{Port=3465}),"local-alice","local-alice");
        await f.Service.Schedule(d.Id,d.Account,d.Message(),new(DateTimeOffset.UtcNow.AddMinutes(1),"UTC",MissedSend.SendWhenAvailable,0,d.Version));
        Due(f.Store,d.Id,120);
        await Task.WhenAll(f.Service.Deliver(d.Id),new ProtocolMailService(f.Store).Deliver(d.Id));
        using var reader=new ImapClient();reader.ServerCertificateValidationCallback=(_,cert,_,_)=>cert?.GetCertHashString(HashAlgorithmName.SHA256)==pin;
        await reader.ConnectAsync("127.0.0.1",3993,SecureSocketOptions.SslOnConnect);await reader.AuthenticateAsync("bob@localhost","local-bob");await reader.Inbox.OpenAsync(FolderAccess.ReadOnly);
        var hits=await reader.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains(d.Subject));
        MailChecks.Check(hits.Count==1 && (await f.Service.Deliver(d.Id)).State=="sent" && f.Drafts.List(d.Account).Count==0,"Two dispatchers sent twice or left active draft");
        await PluginChecks.Reject(()=>f.Service.Cancel(d.Id),"取消");await reader.DisconnectAsync(true);
        Console.WriteLine("PASS missed-send catch-up by two competing dispatchers, one real SMTP/IMAP delivery and no post-send recall");
    }
}
