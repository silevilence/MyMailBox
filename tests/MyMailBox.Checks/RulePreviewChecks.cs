using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class ReceiveRuleChecks
{
    public static async Task Preview()
    {
        var store=new SqliteMailStore(Path.Combine(PluginChecks.Repository,"artifacts/checks/rule-preview-"+Guid.NewGuid().ToString("N")));
        var protocols=new ProtocolMailService(store);var search=new MailSearchService(store,protocols);var service=new ReceiveRuleService(store,search,protocols);
        var dest=await protocols.Create(SqliteMailStore.LocalAccount,null,"复核",default);
        await Import(store,"Order 01");await Import(store,"Order private");
        var rule=new ReceiveRule(Guid.NewGuid(),0,"范围预览",SqliteMailStore.LocalAccount,SqliteMailStore.Inbox,"body:fixture AND subject:Order","subject:private",
            [new(RuleActionKind.Move,Destination:dest)],1,true,false,true);
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="PRAGMA data_version";var before=(long)c.ExecuteScalar()!;
        var p=await service.Preview(rule);
        MailChecks.Check(p.Total==2 && p.Matches==1 && p.Exceptions==1 && p.Effects.Single().Contains("复核") && p.Samples.Any(s=>s.Contains("被例外排除")) &&
            before==(long)c.ExecuteScalar()!, "Dry run wrote database or lost scope/exception");
        var absent=await service.Preview(rule with{Condition="subject:Order OR plugin.missing.v1@check=x"});
        MailChecks.Check(absent.Matches==0 && absent.Unknown==2 && absent.Warnings.Count==1,"Unavailable condition broadened preview");
        await service.Save(rule);var id=await Import(store,"Order 02");
        c.CommandText="UPDATE accounts SET enabled=0 WHERE id=$a";c.Parameters.AddWithValue("$a",SqliteMailStore.LocalAccount.ToString());c.ExecuteNonQuery();
        await service.Process();var run=(await service.Dashboard()).Runs.Single();
        MailChecks.Check(run.State=="paused" && run.Detail.Contains("停用") && (await service.Actions(run.Id)).Count==0,"Changed account did not pause execution");
        c.CommandText="UPDATE accounts SET enabled=1 WHERE id=$a";c.ExecuteNonQuery();
        await service.Retry(run.Id);await service.Process();
        var step=(await service.Actions(run.Id)).Single();MailChecks.Check(step.Undo.HasValue,"No undo classification for local move");
        await service.Undo(step.Operation);MailChecks.Check((await service.Actions(run.Id)).Single().State=="undone" &&
            (await protocols.Messages(SqliteMailStore.LocalAccount,SqliteMailStore.Inbox,0,10)).Items.Any(m=>m.Key==id.ToString()),"Rule move did not undo");
        Console.WriteLine("PASS read-only draft preview even with cold metadata, exception/target details, unknown extension, permission pause, scoped retry and local undo");
    }
}
