using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static partial class AutomationChecks
{
    public static async Task Plugins()
    {
        var f=Fixture();var local=SqliteMailStore.LocalAccount;var inbox=SqliteMailStore.Inbox;var mail=await Put(f.Store,inbox,"插件工作");
        var pages=new Contributions();await using var runtime=new PluginRuntime(pages);
        var def=PluginChecks.Example();string[] capabilities=["automation.condition","automation.action","automation.schedule","mail.write"];
        def=def with{Manifest=def.Manifest with{Entry="automation-example",Permissions=capabilities}};
        var options=new PluginOptions{InstanceId="addon",Grants=capabilities.Select(x=>x+":"+local).ToArray()};
        await runtime.Start(def,options);
        var service=new MailAutomationService(f.Store,f.Search,f.Protocols,f.Time,extensions:()=>pages.AutomationExtensions);
        AutomationBinding Bind(string id,string key,string value)=>new("addon/"+id,1,new(){{key,value}});
        var p=ArchiveDefinition(local,inbox,inbox) with{Archive=null,PluginCondition=Bind("minimum","minimum","1"),PluginSchedule=Bind("utc-daily","hour","0"),
            Actions=[new(TaskActionKind.Extension){Extension=Bind("category","tag","插件分类")}]};
        p=await service.Save(p);var preview=await service.PreviewActions(p);
        MailChecks.Check(preview.Effects.Single().Contains("插件分类")&&service.Extensions.Count==3,"Missing plugin preview or schema");
        using(var timer=new AutomationRunner(service,null)){await WaitFor(async()=>(await service.Runs(p.Id)).Any(r=>r.Cause.StartsWith("extension/")&&r.Done==1));}
        MailChecks.Check((await f.Protocols.Messages(local,inbox,0,10)).Items.Single(x=>x.Key==mail.ToString()).Labels.Contains("插件分类"),"Plugin action had no real effect");
        var retained=pages.AutomationExtensions.First();await runtime.Stop("addon");var paused=await service.Start(p.Id,"manual/missing");
        MailChecks.Check(paused.State=="paused" && (await service.Plans()).Single().Actions[0].Extension!.Parameters.ContainsKey("tag"),"Missing extension ran or erased parameters");
        try{await retained.Evaluate(new(local,f.Time.Now,new Dictionary<string,decimal?>(),true),new Dictionary<string,string>(),default);throw new Exception("Unloaded callback leaked");}catch(InvalidOperationException){}
        var upgraded=def with{Directory=Path.Combine(PluginChecks.Repository,"samples/ExamplePluginV2/bin/Debug/net10.0"),Manifest=def.Manifest with{Version="2.0.0"}};
        await runtime.Start(upgraded,options);p=await service.Save(p);
        MailChecks.Check(p.Actions[0].Extension is {Version:2} b&&b.Parameters["label"]=="插件分类"&&!b.Parameters.ContainsKey("tag"),"Migration lost label");
        await service.Control(paused.Id,true);MailChecks.Check((await service.Advance(paused.Id)).Done==1,"Frozen old schema did not resume after migration");
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();try{await service.PreviewActions(p,cancel.Token);throw new Exception("Canceled preview completed");}catch(OperationCanceledException){}}
        await runtime.Stop("addon");await runtime.Start(def,options);var rollback=await service.Start(p.Id,"manual/downgrade");
        MailChecks.Check(rollback.State=="paused"&&rollback.Detail.Contains("回退"),"Rollback discarded newer settings");
        await runtime.Stop("addon");await runtime.Start(upgraded,new(){InstanceId="addon",Grants=["automation.condition:"+local]});
        await service.Control(rollback.Id,true);MailChecks.Check((await service.Advance(rollback.Id)).State=="paused","Missing grants executed action");
        Console.WriteLine("PASS three collectible external automation contributions, schema preview, real schedule/mark, cancellation, unload pause, v1→v2 migration, rollback and scoped permissions");
    }
}
