using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static partial class AutomationChecks
{
    private static MimeMessage Incoming(string subject,string id,string sender = "bob@localhost")
    {
        var message = new MimeMessage { Subject = subject, Date = DateTimeOffset.UtcNow, MessageId = id,
            Body = new TextPart("plain") { Text = "自动外发验收正文" } };
        message.From.Add(MailboxAddress.Parse(sender));
        message.To.Add(MailboxAddress.Parse("alice@localhost"));
        message.Headers.Add("Return-Path","<"+sender+">");
        return message;
    }

    private static ReceiveRule OutgoingRule(Guid account,Guid folder,string subject) =>
        new(Guid.NewGuid(),0,"自动外发验收",account,folder,"subject:"+subject,"",
            [new(RuleActionKind.AutoReply){MailArgument="已收到来信"}],1,true,false,true);
    private static AutomationPlan ForwardPlan(Guid account,Guid folder,string subject) =>
        new(Guid.NewGuid(),0,"自动转发验收",new(account,folder,"","count>=1",0,0),true)
        {
            Target=new(account,folder,false,"subject:"+subject,null,null,0,0,false,"",SearchOrder.Newest,20,0,null),
            Actions=[new(TaskActionKind.AutoForward,Argument:"bob@localhost")]
        };

    public static async Task AutomaticMail()
    {
        var f=Fixture();
        var owner=await new AccountService(f.Store).Save(new(null,"本机收发账户","imap-smtp","alice@localhost"));
        await using var smtp=new ProtocolFaultServer(true){AcceptAll=true};
        var endpoint=new MailEndpoint("127.0.0.1",smtp.Port,"alice",CertificateSha256:smtp.Pin);
        await f.Protocols.Save(owner.Id,new(endpoint,endpoint),"fixture","fixture");
        var folder=Guid.NewGuid();
        using(var db=f.Store.Open())
        using(var sql=db.CreateCommand())
        {
            sql.CommandText="INSERT INTO folders(id,account_id,name,path,role) VALUES($id,$a,'缓存','验收 / 缓存','')";
            sql.Parameters.AddWithValue("$id",folder.ToString());sql.Parameters.AddWithValue("$a",owner.Id.ToString());sql.ExecuteNonQuery();
        }
        var rules=new ReceiveRuleService(f.Store,f.Search,f.Protocols);
        var rule=OutgoingRule(owner.Id,folder,"incoming");
        await PluginChecks.Reject(()=>rules.Save(rule),"试运行");
        var preview=await rules.Preview(rule);
        MailChecks.Check(preview.MailApproval!=null && smtp.Submissions==0,"Rule preview submitted mail");
        rule=await rules.Save(rule with { MailApproval=preview.MailApproval });
        await PluginChecks.Reject(()=>rules.Save(rule with { Condition="subject:other" }),"试运行");

        async Task Import(MimeMessage m)
        {
            using(m) { using var buffer=new MemoryStream();await m.WriteToAsync(buffer);
                var bytes=buffer.ToArray();await f.Store.Import(folder,bytes,await new MyMailBox.Mail.MimeReader().Parse(bytes)); }
            await rules.Process();
        }
        await Import(Incoming("incoming","one@example.test"));
        MailChecks.Check(smtp.Submissions==1 && smtp.Received!.Contains("Auto-Submitted: auto-replied"),"Approved rule did not submit an auto-reply");
        await Import(Incoming("incoming","one@example.test"));
        await Import(Incoming("incoming","two@example.test"));
        var report=Incoming("incoming","report@example.test","other@localhost");report.Body=new Multipart("report"){new TextPart("plain"){Text="report"}};await Import(report);
        var loop=Incoming("incoming","loop@example.test","other@localhost");loop.Headers.Add("Auto-Submitted","auto-replied");await Import(loop);
        var bounce=Incoming("incoming","bounce@example.test","other@localhost");bounce.Headers.Replace("Return-Path","<>");await Import(bounce);
        var self=Incoming("incoming","self@example.test","alice@localhost");await Import(self);
        MailChecks.Check(smtp.Submissions==1,"Duplicate, daily limit, report, self or bounce produced another reply");

        smtp.DropAfterData = true;
        await Import(Incoming("incoming","uncertain@example.test","uncertain@localhost"));
        smtp.DropAfterData = false;
        var unknown = (await f.Protocols.List(owner.Id,0,20)).Single(m => m.State=="unknown");
        await Import(Incoming("incoming","uncertain@example.test","uncertain@localhost"));
        MailChecks.Check((await f.Protocols.Deliver(unknown.Id)).State=="unknown" && smtp.Submissions==2,
            "Lost automatic-mail acknowledgement caused a second SMTP submission");

        var plan=ForwardPlan(owner.Id,folder,"forward") with { When=new(SqliteMailStore.LocalAccount,SqliteMailStore.Inbox,"","free>=0",0,0) };
        await PluginChecks.Reject(()=>f.Auto.Save(plan),"预览");
        var impact=await f.Auto.PreviewActions(plan);
        MailChecks.Check(impact.Effects.Single().Contains("bob@localhost") && impact.Effects.Single().Contains("附件"),"Forward preview omitted recipient or content scope");
        plan=await f.Auto.Save(plan with { Approval=impact.Approval });
        await PluginChecks.Reject(()=>f.Auto.Save(plan with { Actions=[new(TaskActionKind.AutoForward,Argument:"changed@localhost")] }),"预览");
        await PluginChecks.Reject(()=>f.Auto.PreviewActions(plan with { Actions=[new(TaskActionKind.AutoForward,Argument:"a@localhost,b@localhost")] }),"固定");
        for(int i=0;i<12;i++) await Import(Incoming("forward","f"+i+"@example.test"));
        async Task<AutomationRun> Execute(AutomationPlan definition)
        {
            var run=await f.Auto.Start(definition.Id,"manual/"+Guid.NewGuid());
            for(int i=0;i<30 && run.State is "ready" or "running";i++) run=await f.Auto.Advance(run.Id);
            return run;
        }
        var first=await Execute(plan);
        MailChecks.Check(first.State=="done" && smtp.Submissions==10,"Hourly budget did not bound actual deliveries: "+first.Detail+" / "+smtp.Submissions);
        var second=await Execute(plan);
        MailChecks.Check(second.State=="done" && smtp.Submissions==10,"Re-running a frozen selection sent more copies");
        var frozen=await f.Auto.Start(plan.Id,"manual/disable");
        await f.Auto.Save(plan with { Enabled=false });
        frozen=await f.Auto.Advance(frozen.Id);
        MailChecks.Check(smtp.Submissions==10 && (frozen.State=="paused" || frozen.Failed>0),"Disabled plan still authorized an already-frozen outbound action");
        Console.WriteLine("PASS automatic mail: approval bound to recipients/scope, explicit reapproval, one copy across runs, hourly/per-sender limits, report/bounce/loop/self suppression, disabled queued plan");
    }

    public static async Task AutomaticMailLive()
    {
        var f=Fixture();
        var owner=await new AccountService(f.Store).Save(new(null,"本机真实收发账户","imap-smtp","alice@localhost"));
        string pin=File.ReadAllText(Path.Combine(PluginChecks.Repository,"artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        var ep=new MailEndpoint("127.0.0.1",3993,"alice@localhost",CertificateSha256:pin);
        await f.Protocols.Save(owner.Id,new(ep,ep with {Port=3465}),"local-alice","local-alice");
        string subject="automatic-"+Guid.NewGuid().ToString("N");
        var folder=await f.Protocols.Create(owner.Id,null,subject);
        await f.Protocols.Sync(owner.Id,subject,20);
        var rules=new ReceiveRuleService(f.Store,f.Search,f.Protocols);
        var rule=OutgoingRule(owner.Id,folder,subject) with {IncludeHistory=false};
        rule=await rules.Save(rule with { MailApproval=(await rules.Preview(rule)).MailApproval });
        async Task<ImapClient> Connect(string user)
        {
            var client=new ImapClient();
            client.ServerCertificateValidationCallback=(_,cert,_,_)=>cert?.GetCertHashString(HashAlgorithmName.SHA256)==pin;
            await client.ConnectAsync("127.0.0.1",3993,SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync(user+"@localhost","local-"+user);
            return client;
        }
        using var alice=await Connect("alice");using var bob=await Connect("bob");
        var remote=await alice.GetFolderAsync(subject);
        using var incoming=Incoming(subject,subject+"@example.test");
        var builder=new BodyBuilder{TextBody="自动外发验收正文"};builder.Attachments.Add("private.txt","should stay local"u8.ToArray());incoming.Body=builder.ToMessageBody();
        await remote.AppendAsync(new AppendRequest(incoming,MessageFlags.None));
        await f.Protocols.Sync(owner.Id,subject,20);await rules.Process();
        var plan=ForwardPlan(owner.Id,folder,subject);
        plan=await f.Auto.Save(plan with { Approval=(await f.Auto.PreviewActions(plan)).Approval });
        var run=await f.Auto.Start(plan.Id,"manual/test");
        for(int i=0;i<5 && run.State is "ready" or "running";i++)run=await f.Auto.Advance(run.Id);
        await bob.Inbox.OpenAsync(FolderAccess.ReadOnly);
        var ids=await bob.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
        var received=new List<MimeMessage>();foreach(var id in ids)received.Add(await bob.Inbox.GetMessageAsync(id));
        try
        {
            MailChecks.Check(received.Count==2 && run.State=="done","Independent IMAP recipient did not get exactly two deliveries: "+received.Count+" / "+run.Detail);
            var reply=received.Single(m=>m.Headers["Auto-Submitted"]=="auto-replied");
            var forward=received.Single(m=>m.Headers["Auto-Submitted"]=="auto-generated");
            MailChecks.Check(reply.TextBody?.Trim()=="已收到来信" && reply.InReplyTo==incoming.MessageId &&
                forward.TextBody?.Trim()=="自动外发验收正文" && !forward.Attachments.Any() && forward.Headers.Contains("X-MyMailBox-Automation"),
                "Reply context or forward privacy boundary lost in transport");
            await remote.AppendAsync(new AppendRequest(reply,MessageFlags.None));await f.Protocols.Sync(owner.Id,subject,20);await rules.Process();
            MailChecks.Check((await bob.Inbox.SearchAsync(SearchQuery.SubjectContains(subject))).Count==2,"Delivered automatic reply caused a loop");
        }
        finally {foreach(var message in received)message.Dispose();}
        Console.WriteLine("PASS real GreenMail IMAP arrival -> guarded automatic reply and forward -> independent SMTP recipient/IMAP MIME checks; no forwarded attachment or reply loop");
    }
}
