using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Text.Json;

internal static partial class AutomationChecks
{
    public static async Task History()
    {
        var f=Fixture();var notices=new List<string>();var auto=new MailAutomationService(f.Store,f.Search,f.Protocols,f.Time,notices.Add);
        var local=SqliteMailStore.LocalAccount;var source=SqliteMailStore.Inbox;var dest=await f.Protocols.Create(local,null,"机密目录");
        await Put(f.Store,source,"机密邮件甲");await Put(f.Store,source,"机密邮件乙");
        var p=await auto.Save(ArchiveDefinition(local,source,dest) with{Archive=null,Actions=[new(TaskActionKind.Copy,dest),new(TaskActionKind.Notify,Argument:"密文通知")]});
        var run=await auto.Start(p.Id,"manual/private@example.test");run=await auto.Advance(run.Id);await auto.Cancel(run.Id);await auto.Advance(run.Id);
        var audit=await auto.Audit(run.Id,1,1);MailChecks.Check(audit.Run.State=="canceled"&&audit.Run.Done==1&&audit.Items.Single().State=="canceled"&&
            audit.Selected==2&&audit.Version==p.Version&&audit.Observed?.Result==ConditionResult.True&&(await f.Store.List(dest,0,10)).Items.Count==1,"Cancel changed prior effects or lost frozen gate");
        var json=await auto.ExportAudit(run.Id);
        using(var parsed=JsonDocument.Parse(json)){MailChecks.Check(parsed.RootElement.GetProperty("done").GetInt32()==1&&!json.Contains("private@")&&
            !json.Contains(p.Id.ToString())&&!json.Contains("机密")&&!json.Contains("密文")&&!json.Contains("example.test"),"Audit report leaked content");}
        string path=Path.Combine(Path.GetDirectoryName(f.Store.DatabasePath)!,"files");Directory.CreateDirectory(path);
        p=await auto.Save(p with{Actions=[new(TaskActionKind.Copy,dest),new(TaskActionKind.ExportEml,Argument:path,Failure:StepFailure.Continue),new(TaskActionKind.Notify,Argument:"已执行")]});
        var resumed=await auto.Start(p.Id,"manual/retry-failed");Directory.Move(path,path+".hold");File.WriteAllText(path,"占用");
        for(int i=0;i<2;i++)resumed=await auto.Advance(resumed.Id);
        MailChecks.Check(resumed.Failed==2&&resumed.State=="done"&&notices.Count(s=>s=="已执行")==2,"Continued failures missing");
        File.Move(path,path+".marker");Directory.Move(path+".hold",path);
        await auto.Control(resumed.Id,true);for(int i=0;i<2;i++)resumed=await auto.Advance(resumed.Id);
        MailChecks.Check(resumed.Done==2&&resumed.Failed==0&&notices.Count(s=>s=="已执行")==2&&(await f.Store.List(dest,0,10)).Items.Count==3,"Retry repeated completed steps");
        var retained=await auto.Start(p.Id,"manual/paused");await auto.Control(retained.Id,false);
        f.Time.Now=f.Time.Now.AddDays(91);
        using(var runner=new AutomationRunner(auto,null)){await WaitFor(async()=>!(await auto.Runs(Guid.Empty)).Any(r=>r.Id==run.Id));}
        MailChecks.Check((await auto.Runs(Guid.Empty)).Single().Id==retained.Id,"Retention removed paused work");
        await auto.Cancel(retained.Id);
        try{await auto.Start(p.Id,"manual/private@example.test");throw new Exception("Retired cause replayed");}catch(InvalidDataException){}
        Console.WriteLine("PASS paged run audit, frozen gate/version/selection, truthful cancel, allowlisted export, retry failed steps only, 90-day retention with replay tombstone");
    }
}
