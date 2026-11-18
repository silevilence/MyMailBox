using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Text;

internal static partial class AutomationChecks
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now; }
    private static async Task<Guid> Put(SqliteMailStore store,Guid folder,string subject)
    {
        var raw=Encoding.UTF8.GetBytes("From: sender@example.test\r\nSubject: "+subject+"\r\nDate: Tue, 01 Apr 2025 12:00:00 +0000\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n样本");
        return await store.Import(folder,raw,await new MyMailBox.Mail.MimeReader().Parse(raw));
    }
    private static (SqliteMailStore Store,ProtocolMailService Protocols,MailSearchService Search,MailAutomationService Auto,Clock Time) Fixture()
    {
        var store=new SqliteMailStore(Path.Combine(PluginChecks.Repository,"artifacts/checks/automation-"+Guid.NewGuid().ToString("N")));
        var protocols=new ProtocolMailService(store);var search=new MailSearchService(store,protocols);var clock=new Clock();return(store,protocols,search,new(store,search,protocols,clock),clock);
    }
    public static async Task Conditions()
    {
        var f=Fixture();var p=await f.Auto.Save(new(Guid.NewGuid(),0,"阈值",new(SqliteMailStore.LocalAccount,SqliteMailStore.Inbox,"subject:keep","(count>=2 AND NOT exists=0) OR last=1",0,3),true));
        // With no previous execution last is unknown. Use a determinate expression to establish baseline.
        MailChecks.Check((await f.Auto.Inspect(p)).Result==ConditionResult.Unknown,"Missing last result treated as zero");
        p=await f.Auto.Save(p with{When=p.When with{Expression="count>=2",Hysteresis=1}});
        MailChecks.Check(!(await f.Auto.Inspect(p,true)).Crossed,"Initial sample fired");
        var first=await Put(f.Store,SqliteMailStore.Inbox,"keep one");var second=await Put(f.Store,SqliteMailStore.Inbox,"keep two");await Put(f.Store,SqliteMailStore.Inbox,"excluded");
        var report=await f.Auto.Inspect(p,true);MailChecks.Check(report.Crossed && report.Values.Single(v=>v.Id=="logical").Value>0 && report.Values.Single(v=>v.Id=="disk").Value>0 &&
            report.Values.Single(v=>v.Id=="all").Value==0 && report.Values.Single(v=>v.Id=="count").Value==2,"Metric coverage or crossing incorrect");
        using var db=f.Store.Open();using var c=db.CreateCommand();c.CommandText="DELETE FROM messages WHERE id=$id";c.Parameters.AddWithValue("$id",first.ToString());c.ExecuteNonQuery();
        MailChecks.Check((await f.Auto.Inspect(p,true)).Result==ConditionResult.True,"Hysteresis released at threshold noise");
        c.Parameters["$id"].Value=second.ToString();c.ExecuteNonQuery();MailChecks.Check((await f.Auto.Inspect(p,true)).Result==ConditionResult.False,"Gate did not rearm");
        await Put(f.Store,SqliteMailStore.Inbox,"keep new");await Put(f.Store,SqliteMailStore.Inbox,"keep more");
        MailChecks.Check(!(await f.Auto.Inspect(p,true)).Crossed,"Cooldown ignored");
        var unknown=await f.Auto.Inspect(p with{When=p.When with{Expression="NOT quota>=1"}});
        MailChecks.Check(unknown.Result==ConditionResult.Unknown,"Unsupported quota became a positive trigger");
        Console.WriteLine("PASS nested task conditions, independent filter, logical/cached/allocated metrics, unknown, threshold crossing, hysteresis and cooldown");
    }
}
