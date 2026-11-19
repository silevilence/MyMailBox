using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static class MailboxProviderChecks
{
    // Runs on a configured account containing at least one example message. Nothing is sent or changed.
    internal static async Task Probe(IMailboxProvider provider, MailAccount account, string? inboxId = null)
    {
        var identity = new MailboxIdentity(account.Id,account.Address);
        await provider.Connect(identity,default);
        var folders = await provider.Folders(identity,default);
        var inbox = inboxId ?? folders.First(f => f.Role == "inbox").Id;
        var page = await provider.List(identity,inbox,null,10,default);
        var first = page.Items.First();
        using var original = new MemoryStream();
        await provider.Read(identity,inbox,first.Id,original,default);
        var parsed = await new MimeReader().Parse(original.ToArray());
        PluginChecks.Check(parsed.Subject == first.Subject,"Provider summary and MIME refer to different messages");
        if (page.Next != null)
        {
            var next = await provider.List(identity,inbox,page.Next,10,default);
            PluginChecks.Check(!page.Items.Select(m=>m.Id).Intersect(next.Items.Select(m=>m.Id)).Any(),"Overlapping message pages");
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await provider.Folders(identity,canceled.Token); throw new Exception("Provider ignored cancellation"); }
        catch (OperationCanceledException) { }
    }

    public static async Task Run()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!,"provider-contract");
        var store = new SqliteMailStore(root);
        var pages = new Contributions();
        var accounts = new AccountService(store,() => pages.MailboxProviders);
        var services = new ProtocolMailService(store) { MailboxProviders = () => pages.MailboxProviders };
        using var graph = new GraphMailService(store);
        var local = BuiltinMailboxAdapters.Create(store,graph,services).Single(p=>p.Type.Id == "local");
        var localAccount = await accounts.Save(new(null,"本地契约测试","local"));
        var localIdentity = new MailboxIdentity(localAccount.Id,localAccount.Address);
        var localInbox = (await local.Folders(localIdentity,default)).First(f=>f.Role == "inbox");
        var raw = MailChecks.Sample("提供者原文");
        await local.Upload(localIdentity,localInbox.Id,new MemoryStream(raw),default);
        await Probe(local,localAccount);

        var packages = new PluginPackages(Path.Combine(root,"plugins"),DefaultProfile.Catalog(root));
        var definition = packages.Install(Path.Combine(PluginChecks.Repository,"artifacts/packages/example-mailbox-1.0.0.zip"));
        await using var runtime = new PluginRuntime(pages);
        var config = new PluginOptions { InstanceId = "mailbox" };
        await runtime.Start(definition,config);
        var account = await accounts.Save(new(null,"外部邮箱",accounts.ProviderTypes.Single().Id,"fixture@example.invalid"));
        var a = new MailboxIdentity(account.Id,account.Address);
        var unavailable = pages.MailboxProviders.Single();
        await PluginChecks.Reject(() => unavailable.Connect(a,default),"未授予权限");
        await runtime.Stop("mailbox"); await PluginChecks.Reject(() => unavailable.Connect(a,default),"停止接单");
        PluginChecks.Check((await accounts.List()).Any(m=>m.Id == account.Id && m.Provider=="mailbox/loopback"),"Unloading removed account");
        config = new PluginOptions { InstanceId = "mailbox", Grants = ["mail.read:"+a.Id,"mail.write:"+a.Id,"mail.send:"+a.Id] };
        await runtime.Start(definition,config);
        var provider = pages.MailboxProviders.Single();
        await PluginChecks.Reject(() => provider.Folders(new(Guid.NewGuid(),a.Address),default),"未授予权限");
        var op = Guid.NewGuid();
        await services.Enqueue(op,a.Id,new(a.Address,"","","契约投递","回环正文"));
        PluginChecks.Check((await services.Deliver(op)).State=="sent","Outbox rejected external provider");
        await Probe(provider,account);
        var receive = new MailboxReceiveProvider(store,() => pages.MailboxProviders);
        var unified = new UnifiedInboxService(store,accounts,receive);
        await receive.Receive(account,50,default);
        var inbox = (await unified.Inbox(a.Id,null,25)).Messages.Single();
        PluginChecks.Check((await unified.Open(a.Id,inbox.Position.Key)).Contains("回环正文"),"Unified reader lost MIME body");
        PluginChecks.Check((await unified.Inbox(a.Id,null,25)).Messages.Single().Cached,"Missing offline copy");
        var sent = (await provider.Search(a,"inbox","契约",null,10,default)).Items.Single();
        var uploaded = await provider.Upload(a,"inbox",new MemoryStream(raw),default);
        await provider.Mark(a,"inbox",uploaded,true,default);
        var changed = await provider.Delta(a,"inbox",null,100,default);
        PluginChecks.Check(changed.Changed.Any(m => m.Id==uploaded && m.Read),"Missing mark delta");
        var moved = await provider.Move(a,"inbox",uploaded,"archive",default);
        var changes = await provider.Delta(a,"inbox",changed.Cursor,100,default);
        PluginChecks.Check(changes.Removed.SequenceEqual([uploaded]),"Missing move tombstone");
        using var copy = new MemoryStream(); await provider.Read(a,"archive",moved,copy,default);
        PluginChecks.Check(raw.SequenceEqual(copy.ToArray()) && (await provider.Quota(a,default)).Used>=raw.Length,"Provider changed upload or quota");
        await provider.Move(a,"inbox",sent.Id,"archive",default);
        await receive.Receive(account,50,default);
        PluginChecks.Check((await unified.Inbox(a.Id,null,25)).Messages.Count==0,"Tombstone ignored by unified inbox");
        await runtime.Stop("mailbox"); await runtime.Start(definition,config);
        PluginChecks.Check((await pages.MailboxProviders.Single().List(a,"archive",null,10,default)).Items.Length==2,"Reload lost provider data");
        var rejected = Guid.NewGuid(); await services.Enqueue(rejected,a.Id,new("external@example.invalid","","","退信","无外部投递"));
        PluginChecks.Check((await services.Deliver(rejected)).State=="rejected","Loopback did not limit delivery to this account");
        await runtime.Stop("mailbox");
        Console.WriteLine("PASS mailbox provider: packaged extension, scoped grants, normal outbox, shared reader, search/upload/move/flags/quota, durable delta, unified inbox, unload/reload");
    }
}
