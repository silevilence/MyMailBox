using MyMailBox.Contracts;
using MyMailBox.Mail;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using System.Security.Cryptography;

internal static partial class AutomationChecks
{
    private static AutomationPlan ArchiveDefinition(Guid account,Guid source,Guid root)=>new AutomationPlan(Guid.NewGuid(),0,"旧邮件归档",
        new(account,source,"","exists=1",0,0),true)
    {
        Target=new(account,source,false,"",null,null,30,0,true,"保留",SearchOrder.Oldest,100,0,null),
        Archive=new(SqliteMailStore.LocalAccount,root,false,540,"UTC")
    };
    public static async Task Archive()
    {
        var f=Fixture();
        var source=SqliteMailStore.Inbox;var root=await f.Protocols.Create(SqliteMailStore.LocalAccount,null,"归档");
        var p=await f.Auto.Save(ArchiveDefinition(SqliteMailStore.LocalAccount,source,root));
        MailChecks.Check((await f.Auto.Start(p.Id,"before")).State=="skipped","Empty condition scheduled work");
        var first=await Put(f.Store,source,"旧邮件");var second=await Put(f.Store,source,"受保护");var other=await Put(f.Store,source,"继续");
        var before=await f.Auto.Select(p);
        MailChecks.Check(before.Items.Count==3&&(await f.Store.List(source,0,10)).Items.Count==3 && !(await f.Store.Folders()).Any(x=>x.ParentId==root),"Preview mutated archive");
        var run=await f.Auto.Start(p.Id,"manual");
        await f.Auto.Control(run.Id,false);MailChecks.Check((await f.Auto.Advance(run.Id)).State=="paused","Pause ignored");
        var notifications=new List<string>();var restart=new MailAutomationService(f.Store,f.Search,f.Protocols,f.Time,notifications.Add);
        await restart.Control(run.Id,true);await f.Protocols.Mark(SqliteMailStore.LocalAccount,source,[second.ToString()],MailMark.Star,"");
        MailChecks.Check((await restart.Start(p.Id,"overlap")).Id==run.Id,"Overlapping runs");
        for(int i=0;i<3;i++)await Task.WhenAll(restart.Advance(run.Id),f.Auto.Advance(run.Id));
        run=(await restart.Runs(p.Id)).Single(r=>r.Cause=="manual");
        var year=(await f.Store.Folders()).Single(x=>x.ParentId==root&&x.Name=="2025");
        var month=(await f.Store.Folders()).Single(x=>x.ParentId==year.Id&&x.Name=="04");
        MailChecks.Check(run.State=="done"&&run.Done==2&&run.Skipped==1&&(await f.Store.List(month.Id,0,10)).Items.Count==2&&
            (await f.Store.List(source,0,10)).Items.Single().Id==second&&(await f.Auto.Start(p.Id,"manual")).Id==run.Id,"Restart duplicated/lost messages or removed star");
        await Put(f.Store,source,"每日样本");p=await restart.Save(p with{Archive=p.Archive! with{Daily=true,LocalMinute=0}});
        using(var runner=new AutomationRunner(restart,null))
        {
            var deadline=DateTime.UtcNow.AddSeconds(12);
            while(DateTime.UtcNow<deadline && !(await restart.Runs(p.Id)).Any(r=>r.Cause.StartsWith("daily/")&&r.State=="done"))await Task.Delay(50);
        }
        MailChecks.Check((await restart.Runs(p.Id)).Count(r=>r.Cause.StartsWith("daily/")&&r.Done==1)==1&&notifications.Any(),"Daily archive or notification missing");
        Console.WriteLine("PASS daily archive preview, month folders, protected/changed selection, overlap, persisted pause/restart and completion notification");
    }
    public static async Task ArchiveLive()
    {
        var f=Fixture();var accounts=new AccountService(f.Store);var account=await accounts.Save(new(null,"归档来源","imap","alice@localhost"));
        var pin=File.ReadAllText(Path.Combine(PluginChecks.Repository,"artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        await f.Protocols.Save(account.Id,new(new("127.0.0.1",3993,"alice@localhost",CertificateSha256:pin)),"local-alice",null);
        var path="auto-"+Guid.NewGuid().ToString("N");var source=await f.Protocols.Create(account.Id,null,path);
        using var peer=new ImapClient();peer.ServerCertificateValidationCallback=(_,cert,_,_)=>cert?.GetCertHashString(HashAlgorithmName.SHA256)==pin;
        await peer.ConnectAsync("127.0.0.1",3993,SecureSocketOptions.SslOnConnect);await peer.AuthenticateAsync("alice@localhost","local-alice");
        var server=await peer.GetFolderAsync(path);
        using var mime=new MimeMessage{Subject="远端旧邮件",Date=new DateTimeOffset(2025,4,1,12,0,0,TimeSpan.Zero),Body=new TextPart("plain"){Text="完整原文"}};
        await server.AppendAsync(new MailKit.AppendRequest(mime,MailKit.MessageFlags.None){InternalDate=mime.Date});await f.Protocols.Sync(account.Id,path,20);
        var root=await f.Protocols.Create(SqliteMailStore.LocalAccount,null,"归档");var p=await f.Auto.Save(ArchiveDefinition(account.Id,source,root));
        var run=await f.Auto.Start(p.Id,"mail");MailChecks.Check(run.State=="ready","IMAP archive did not match: "+run.Detail);
        void Sql(string sql){using var db=f.Store.Open();using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
        Sql("CREATE TRIGGER full_disk BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT,'full disk'); END");
        run=await f.Auto.Advance(run.Id);
        MailChecks.Check(run.State=="failed"&&run.Failed==1&&(await f.Protocols.Messages(account.Id,source,0,20)).Items.Count==1,"Failed target write damaged IMAP source");
        Sql("DROP TRIGGER full_disk");
        var restart=new MailAutomationService(f.Store,f.Search,f.Protocols,f.Time);await restart.Control(run.Id,true);run=await restart.Advance(run.Id);await restart.Advance(run.Id);
        var folders=await f.Store.Folders();var year=folders.Single(x=>x.ParentId==root&&x.Name=="2025");var month=folders.Single(x=>x.ParentId==year.Id&&x.Name=="04");
        var archived=(await f.Store.List(month.Id,0,10)).Items.Single();
        MailChecks.Check(run.State=="done"&&run.Done==1 && (await f.Protocols.Messages(account.Id,source,0,20)).Items.Count==0&&
            (await new MyMailBox.Mail.MimeReader().Parse(await f.Store.Read(archived.Id))).Text.Contains("完整原文"),"Verified cross-store archive failed: "+run.Detail);
        await peer.DisconnectAsync(true);
        Console.WriteLine("PASS real IMAP archive, target write failure preserves source, resumed month archive verified before expunge, no duplicates");
    }
}
