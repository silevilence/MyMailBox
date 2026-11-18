using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class AutomationChecks
{
    public static async Task Actions()
    {
        var f=Fixture();var messages=new List<string>();var service=new MailAutomationService(f.Store,f.Search,f.Protocols,f.Time,messages.Add);
        var source=SqliteMailStore.Inbox;var local=SqliteMailStore.LocalAccount;var dest=await f.Protocols.Create(local,null,"动作副本");
        string directory=Path.Combine(Path.GetDirectoryName(f.Store.DatabasePath)!,"files");Directory.CreateDirectory(directory);
        var builder=new BodyBuilder{TextBody="导出测试"};builder.Attachments.Add("材料.bin",new byte[]{0,1,2,255});
        using var mime=new MimeMessage{Subject="组合",Date=DateTimeOffset.UtcNow.AddDays(-100),Body=builder.ToMessageBody()};
        using var stream=new MemoryStream();mime.WriteTo(stream);var raw=stream.ToArray();
        var original=await f.Store.Import(source,raw,await new MyMailBox.Mail.MimeReader().Parse(raw));
        var p=ArchiveDefinition(local,source,dest) with{Archive=null,Actions=[
            new(TaskActionKind.Copy,dest),new(TaskActionKind.Label,Argument:"财务"),new(TaskActionKind.Read),
            new(TaskActionKind.SaveAttachments,Argument:directory),new(TaskActionKind.ExportEml,Argument:directory,Failure:StepFailure.Retry,Retries:2),
            new(TaskActionKind.Notify,Argument:"处理完成"),new(TaskActionKind.Trash)]};
        async Task Reject(AutomationPlan candidate){try{await service.Save(candidate);throw new Exception("Invalid action accepted");}catch(InvalidDataException){}}
        await Reject(p);var preview=await service.PreviewActions(p);
        MailChecks.Check(preview.Selected==1&&preview.Effects.Last().Contains("垃圾箱")&&(await f.Store.List(dest,0,5)).Items.Count==0,"Destructive preview incorrect");
        p=await service.Save(p with{Approval=preview.Approval});
        await Reject(p with{Actions=[new(TaskActionKind.DeletePermanently)]});
        await Reject(p with{Actions=[new(TaskActionKind.Copy,Guid.NewGuid())]});
        var run=await service.Start(p.Id,"manual");Directory.Move(directory,directory+".held");File.WriteAllText(directory,"占用路径");
        run=await service.Advance(run.Id);var steps=await service.Steps(run.Id);
        MailChecks.Check(run.State=="failed"&&steps.Count==4&&steps[0].State=="done"&&(await f.Store.List(dest,0,5)).Items.Count==1,"Failed step replayed copy or ignored policy");
        File.Move(directory,directory+".held-file");Directory.Move(directory+".held",directory);
        await service.Control(run.Id,true);run=await service.Advance(run.Id);steps=await service.Steps(run.Id);
        MailChecks.Check(run.State=="done"&&run.Done==1&&steps.Count==7&&steps[0].Attempts==1&&steps[3].Attempts==2&&messages.Contains("处理完成")&&
            (await f.Store.List(source,0,5)).Items.Count==0 && (await f.Store.List(dest,0,5)).Items.Count==1,"Ordered actions failed: "+run.Detail);
        MailChecks.Check(File.ReadAllBytes(Directory.GetFiles(directory,"*.eml").Single()).SequenceEqual(raw)&&
            File.ReadAllBytes(Directory.GetFiles(directory,"*.bin",SearchOption.AllDirectories).Single()).SequenceEqual(new byte[]{0,1,2,255}),"Export fidelity failure");
        await Put(f.Store,source,"重试");p=await service.Save(p with{Actions=[new(TaskActionKind.ExportEml,Argument:directory,Failure:StepFailure.Retry,Retries:2),new(TaskActionKind.Notify,Argument:"后续")],Approval=null});
        run=await service.Start(p.Id,"retry");Directory.Move(directory,directory+".output");File.WriteAllText(directory,"占用");
        run=await service.Advance(run.Id);MailChecks.Check(run.State=="failed"&&(await service.Steps(run.Id)).Single().Attempts==3&&!messages.Contains("后续"),"Retry bound or stop policy ignored");
        File.Move(directory,directory+".held-again");Directory.Move(directory+".output",directory);await service.Control(run.Id,true);await service.Advance(run.Id);
        p=await service.Save(p with{Actions=[new(TaskActionKind.ExportEml,Argument:directory,Failure:StepFailure.Continue),new(TaskActionKind.Notify,Argument:"继续后续")]});
        run=await service.Start(p.Id,"continue");Directory.Move(directory,directory+".backup");File.WriteAllText(directory,"占用");
        run=await service.Advance(run.Id);
        MailChecks.Check(run.Failed==1&&messages.Contains("继续后续")&&(await service.Steps(run.Id))[0].State=="continued","Continue policy hid failure");
        p=p with{Actions=[new(TaskActionKind.DeletePermanently)]};preview=await service.PreviewActions(p);
        MailChecks.Check(preview.Effects.Single().Contains("无法撤销"),"Permanent removal unclear");
        p=await service.Save(p with{Approval=preview.Approval});run=await service.Start(p.Id,"delete");run=await service.Advance(run.Id);
        MailChecks.Check(run.Done==1&&(await f.Store.List(source,0,5)).Items.Count==0,"Explicitly approved permanent deletion failed");
        Console.WriteLine("PASS ordered copy/marks/labels/EML/attachments/notification, impact-bound delete approval, trash vs permanent, stop/continue/bounded retry and step recovery");
    }
}
