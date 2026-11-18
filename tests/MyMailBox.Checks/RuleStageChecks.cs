using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Diagnostics;

internal static partial class ReceiveRuleChecks
{
    private sealed class ExitAfterMove(IMailActionService inner):IMailActionService
    {
        public async Task<IReadOnlyList<MailActionResult>> Apply(MailActionRequest request,CancellationToken token=default)
        {
            var result=await inner.Apply(request,token);
            if(result.Single().Success) Environment.Exit(77);
            return result;
        }
        public Task Undo(Guid operation,CancellationToken token=default)=>inner.Undo(operation,token);
    }

    public static async Task Crash(string directory)
    {
        var store=new SqliteMailStore(directory);var protocols=new ProtocolMailService(store);var search=new MailSearchService(store,protocols);
        await new ReceiveRuleService(store,search,new ExitAfterMove(protocols),protocols).Process();
    }

    public static async Task Stages()
    {
        var directory=Path.Combine(PluginChecks.Repository,"artifacts/checks/rule-recovery-"+Guid.NewGuid().ToString("N"));
        var store=new SqliteMailStore(directory);var protocols=new ProtocolMailService(store);var search=new MailSearchService(store,protocols);
        var rules=new ReceiveRuleService(store,search,protocols);
        var target=await protocols.Create(SqliteMailStore.LocalAccount,null,"归档",default);
        var rule=await rules.Save(new(Guid.NewGuid(),0,"一次归档",SqliteMailStore.LocalAccount,SqliteMailStore.Inbox,"subject:Order","",
            [new(RuleActionKind.Move,Destination:target),new(RuleActionKind.Star)],1,true,false,true));
        var hit=await Import(store,"Order 01");
        var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{typeof(ReceiveRuleChecks).Assembly.Location,"rule-crash-worker",directory})start.ArgumentList.Add(arg);
        using(var child=Process.Start(start)!) {await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));MailChecks.Check(child.ExitCode==77,"Move/crash checkpoint not reached");}
        var run=(await rules.Dashboard()).Runs.Single();
        MailChecks.Check(run.State=="running" && (await rules.Actions(run.Id)).Single().State=="started","Crash did not leave intended durable boundary");
        await rules.Process(); await rules.Process();
        var journal=await rules.Actions(run.Id);
        MailChecks.Check(journal.Count==2 && journal.All(a=>a.State=="done") && journal[0].Undo==journal[0].Operation &&
            (await protocols.Messages(SqliteMailStore.LocalAccount,target,0,10)).Items.Single() is { Starred:true } mail && mail.Key==hit.ToString(),"Lost receipt replay duplicated move or lost subsequent action");
        await Import(store,"Order 02");await rules.Save(rule with{Name="新版本"});await rules.Process();
        MailChecks.Check((await rules.Dashboard()).Runs.Any(x=>x.State=="skipped" && x.Version==1),"Historical event ran changed rule");
        Console.WriteLine("PASS real process exit after local move, operation-ID reconciliation, continuation without repeated effect and rule revision pinning");
    }
}
