using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static partial class AutomationChecks
{
    private static async Task WaitFor(Func<Task<bool>> predicate)
    {
        var end=DateTime.UtcNow.AddSeconds(35);
        while(DateTime.UtcNow<end){if(await predicate())return;await Task.Delay(70);}
        throw new TimeoutException("等待自动化事件超时。");
    }
    public static async Task Events(bool live)
    {
        var f=Fixture();var auto=new MailAutomationService(f.Store,f.Search,f.Protocols,f.Time,_=>{});
        var local=SqliteMailStore.LocalAccount;var folder=SqliteMailStore.Inbox;await Put(f.Store,folder,"事件样本");
        async Task<AutomationPlan> Save(string name,AutomationTrigger trigger)=>await auto.Save(ArchiveDefinition(local,folder,folder) with{
            Name=name,Archive=null,Actions=[new(TaskActionKind.Notify,Argument:name)],Triggers=[trigger]});
        if(!live)
        {
            var root=await Save("启动",new("startup"));
            var child=await Save("成功链",new("task",root.Id,true));var failure=await Save("失败链",new("task",root.Id,false));
            try{await auto.Save(root with{Triggers=[new("task",child.Id)]});throw new Exception("Dependency cycle allowed");}catch(InvalidDataException){}
            await Task.WhenAll(Enumerable.Range(0,12).Select(_=>auto.SystemEvent("startup")));
            using var runner=new AutomationRunner(auto,null);
            await WaitFor(async()=>(await auto.Runs(child.Id)).Any(r=>r.State=="done"));
            MailChecks.Check((await auto.Runs(root.Id)).Count==1 && (await auto.Runs(failure.Id)).Count==0,"Storm or previous outcome filter failed");
            var children=await auto.Runs(child.Id);MailChecks.Check(children[0].Cause.Contains(root.Id.ToString()),"Missing predecessor identity");
            string dir=Path.Combine(Path.GetDirectoryName(f.Store.DatabasePath)!,"export");Directory.CreateDirectory(dir);
            root=await auto.Save(root with{Actions=[new(TaskActionKind.ExportEml,Argument:dir)]});
            Directory.Move(dir,dir+".held");File.WriteAllText(dir,"测试路径不可用");
            var manual=await auto.Start(root.Id,"manual/test");await auto.Advance(manual.Id);
            await WaitFor(async()=>(await auto.Runs(failure.Id)).Any(r=>r.State=="done"));
            Console.WriteLine("PASS event storm coalescing, durable task success/failure causality, dependency cycle rejection and manual run");
            return;
        }
        var owner=await new AccountService(f.Store).Save(new(null,"事件来源","imap-smtp","alice@localhost"));
        var pin=File.ReadAllText(Path.Combine(PluginChecks.Repository,"artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        var incoming=new MailEndpoint("127.0.0.1",3993,"alice@localhost",CertificateSha256:pin);
        await f.Protocols.Save(owner.Id,new(incoming,incoming with{Port=3465}),"local-alice","local-alice");
        var received=await Save("收件",new("received",owner.Id));var sent=await Save("发送",new("sent",owner.Id));var synced=await Save("同步",new("synced",owner.Id));
        var path="events-"+Guid.NewGuid().ToString("N");await f.Protocols.Create(owner.Id,null,path);await f.Protocols.Sync(owner.Id,path,10);
        using var client=new ImapClient();client.ServerCertificateValidationCallback=(_,cert,_,_)=>cert?.GetCertHashString(HashAlgorithmName.SHA256)==pin;
        await client.ConnectAsync("127.0.0.1",3993,SecureSocketOptions.SslOnConnect);await client.AuthenticateAsync("alice@localhost","local-alice");
        var imap=await client.GetFolderAsync(path);using var mime=new MimeKit.MimeMessage{Subject="新到达事件",Body=new MimeKit.TextPart("plain"){Text="内容"}};
        await imap.AppendAsync(mime);await f.Protocols.Sync(owner.Id,path,10);await client.DisconnectAsync(true);
        var delivery=await f.Protocols.Send(owner.Id,new("bob@localhost","","","任务发送事件","本机样本"));
        MailChecks.Check(delivery.State=="sent","Missing SMTP acceptance");
        using var worker=new AutomationRunner(auto,null);
        foreach(var p in new[]{received,sent,synced})await WaitFor(async()=>(await auto.Runs(p.Id)).Any(r=>r.State=="done"));
        var center=new ActivityCenter(f.Store);var activity=(await center.Recent()).First(a=>a.Kind=="received" && a.Account==owner.Id);
        var link=await center.Resolve(activity.Id);MailChecks.Check(link.Mail is {} hit && (await f.Search.Read(hit)).Contains("内容"),"Activity did not open originating IMAP message");
        Console.WriteLine("PASS real IMAP arrival after baseline, IMAP sync completion and acknowledged SMTP delivery trigger saved automation");
    }
}
