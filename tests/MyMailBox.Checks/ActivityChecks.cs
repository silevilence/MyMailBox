using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static class ActivityChecks
{
    public static async Task Run()
    {
        var store=new SqliteMailStore(Path.Combine(PluginChecks.Repository,"artifacts/checks/activity-"+Guid.NewGuid().ToString("N")));_=new AccountService(store);
        var center=new ActivityCenter(store);var sink=new SqliteNotificationSink(store);var reminder=new ReminderService(store,sink);
        var item=await reminder.Create(new("个人提醒内容",DateTime.UtcNow.AddMinutes(5),"UTC"));
        var emitted=new List<ActivityBatch>();var builtin=new NotificationPresenter("builtin","应用",(b,_)=>{emitted.Add(b);return Task.CompletedTask;});
        var contributions=new Contributions();var alternateCalls=new List<ActivityBatch>();using var registration=contributions.AddNotificationPresenter(new("fixture/sink","测试",(b,_)=>{alternateCalls.Add(b);return Task.CompletedTask;}));
        IReadOnlyList<NotificationPresenter> Presenters()=>new[]{builtin}.Concat(contributions.Presenters).ToArray();
        async Task Post(string key,ReminderNotificationMode mode=ReminderNotificationMode.Banner)=>await sink.Publish(new(key,item.Id,item.Title,item.DueUtc,null,mode));
        await Post("first");await Post("second");await Post("first");await Post("silent",ReminderNotificationMode.ListOnly);
        await center.Configure(new(false,true,"fixture/sink"));await center.Dispatch(()=>true,Presenters);
        MailChecks.Check(alternateCalls.Single().Count==2&&!alternateCalls[0].Text.Contains(item.Title)&&emitted.Count==0&&(await center.Recent()).Count==3,"Lock privacy, idempotence, replacement or aggregation failed");
        var resolved=await center.Resolve(alternateCalls.Single().Last);MailChecks.Check(resolved.Record==item.Id&&resolved.Route=="reminder","Notification opened wrong reminder");
        registration.Dispose();await Post("replacement-gone");await center.Dispatch(()=>false,Presenters);
        MailChecks.Check(emitted.Single().Text.Contains(item.Title),"Removed plugin still received data or fallback failed");
        await center.SetEnabled("sync",null,false);await center.SetEnabled("sync",SqliteMailStore.LocalAccount,true);
        MailChecks.Check(await center.Enabled("sync",SqliteMailStore.LocalAccount)&&!await center.Enabled("sync",Guid.NewGuid()),"Per-account type override failed");
        await center.SetEnabled("reminder",null,false);await Post("muted");await center.Dispatch(()=>false,Presenters);
        await center.Configure(new(true,true,"builtin"));center.RecordError("测试错误详情");await center.Dispatch(()=>false,Presenters);
        MailChecks.Check(emitted.Count==1&&(await center.Recent()).First().Detail=="测试错误详情","DND removed error from center or posted an unwanted notification");
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="DELETE FROM reminders WHERE id=$id";c.Parameters.AddWithValue("$id",item.Id.ToString());c.ExecuteNonQuery();
        try{await center.Resolve(alternateCalls[0].Last);throw new Exception("Stale reminder link accepted");}catch(InvalidDataException){}
        Console.WriteLine("PASS activity center, replaceable presenter + unload, lock privacy, DND and per-account preferences, batching/dedup, correct/stale deep links");
    }
}
