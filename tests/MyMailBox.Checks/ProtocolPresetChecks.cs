using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static partial class ProtocolChecks
{
    public static async Task Preset(string id)
    {
        var directory=Path.Combine(PluginChecks.Repository,"artifacts","checks","preset-"+Guid.NewGuid().ToString("N"));
        var pages=new Contributions();await using var runtime=new PluginRuntime(pages);
        var config=DefaultProfile.Ordinary.Instances.Single(i=>i.Id==id);
        var definition=DefaultProfile.Catalog(directory).Find(config);
        var options=new PluginOptions{InstanceId=id};
        await runtime.Start(definition,options);
        var store=new SqliteMailStore(Path.Combine(directory,"mail"));
        var accounts=new AccountService(store);
        var account=await accounts.Save(new(null,"测试预设","imap-smtp","fixture@example.test"));
        var mail=new ProtocolMailService(store,()=>pages.ProtocolPresets);
        var preset=(await mail.Presets()).Single();
        var settings=preset.ForAddress(account.Address);
        MailChecks.Check(preset.Id==id+"/imap-smtp" && settings.Incoming?.Port==993 && settings.Outgoing?.Port==465 &&
            settings.Incoming.Tls==MailTls.Implicit && preset.Guidance.Contains("授权"),"Invalid provider setup");
        await mail.Save(account.Id,settings,null,null);
        await Reject(()=>mail.Save(account.Id,settings with { Incoming=settings.Incoming! with { Host="changed.invalid" } },null,null),"不一致");
        Guid folder=Guid.NewGuid();
        using(var db=store.Open())
        using(var command=db.CreateCommand())
        {
            command.CommandText="INSERT INTO folders(id,account_id,name,path,role) VALUES($f,$a,'收件箱','测试 / 收件箱','inbox')";
            command.Parameters.AddWithValue("$f",folder.ToString());command.Parameters.AddWithValue("$a",account.Id.ToString());command.ExecuteNonQuery();
        }
        var raw="Subject: Cached before unload\r\n\r\nPreserved"u8.ToArray();
        var existing=await store.Import(folder,raw,await new MyMailBox.Mail.MimeReader().Parse(raw));
        await runtime.Stop(id);
        MailChecks.Check((await mail.Presets()).Count==0 && (await mail.Settings(account.Id)).PresetId==preset.Id &&
            (await accounts.List()).Any(a=>a.Id==account.Id) && (await store.Read(existing)).SequenceEqual(raw),"Unload discarded account data");
        // No credentials were stored. A missing preset must stop before creating a connection.
        await Reject(()=>mail.Folders(account.Id),"停用");
        await runtime.Start(definition,options);
        await mail.Save(account.Id,settings,null,null);
        var custom=new ProtocolSettings(new("first.invalid",993,account.Address),new("first.invalid",465,account.Address));
        await mail.Save(account.Id,custom,"test-secret","test-secret");
        await Reject(()=>mail.Save(account.Id,custom with{Incoming=custom.Incoming! with{Host="second.invalid"}},null,null),"重新输入");
        MailChecks.Check((await mail.Settings(account.Id)).Incoming?.Host=="first.invalid","Failed preset change modified active endpoint");
        Console.WriteLine("PASS "+id+" ordinary plug-in, TLS defaults, explicit selection, removal/re-enable preserves settings, credential binding; vendor login not exercised");
    }
}
