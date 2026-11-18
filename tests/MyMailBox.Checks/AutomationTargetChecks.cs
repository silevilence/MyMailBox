using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class AutomationChecks
{
    public static async Task Targets()
    {
        var f=Fixture();var owner=await new LocalMailboxService(f.Store,new AccountService(f.Store),new MyMailBox.Mail.MimeReader()).Create(new(null,"邮箱二","local"));
        var parent=await f.Protocols.Create(owner.Id,null,"项目",default);var child=await f.Protocols.Create(owner.Id,parent,"年度",default);
        var eligible=await Put(f.Store,child,"case 100");var protectedId=await Put(f.Store,child,"case 200");var outside=await Put(f.Store,SqliteMailStore.Inbox,"case 300");
        await f.Protocols.Mark(owner.Id,child,[protectedId.ToString()],MailMark.Star,"");
        var saved=new SavedSearch(Guid.NewGuid(),"编号",@"subject~""case [0-9]+""",SearchOrder.Newest,owner.Id,null);await f.Search.Save(saved);
        var selection=new TargetSelection(owner.Id,parent,true,"",saved.Id,[eligible.ToString(),protectedId.ToString(),outside.ToString()],1,0,true,"保留",SearchOrder.Oldest,10,0,null);
        var p=new AutomationPlan(Guid.NewGuid(),0,"跨邮箱",new(SqliteMailStore.LocalAccount,SqliteMailStore.Inbox,"","count>=1",0,0),true){Target=selection};
        var snapshot=await f.Auto.Select(p);var chosen=snapshot.Items.Single();
        MailChecks.Check(chosen.Id==eligible.ToString() && snapshot.TriggerScope!=snapshot.TargetScope && await f.Auto.Recheck(p,snapshot,chosen) is null,"Scopes, subtree, fixed selection or saved filter diverged");
        await f.Protocols.Mark(owner.Id,child,[eligible.ToString()],MailMark.AddLabel,"保留");
        MailChecks.Check(await f.Auto.Recheck(p,snapshot,chosen) is not null,"New preservation label ignored before destructive action");
        p=p with{Target=selection with{Filter="tag:保留",KeepLabel="",MaxBytes=1}};MailChecks.Check((await f.Auto.Select(p)).Items.Count==0,"Byte limit overflow");
        p=p with{Target=selection with{KeepLabel="",KeepStarred=false,KeepNewest=1,Order=SearchOrder.Largest}};
        MailChecks.Check((await f.Auto.Select(p)).Items.Count==1,"Keep-newest retention failed");
        p=p with{Target=selection with{KeepLabel="",LowWaterBytes=snapshot.ScopeBytes}};
        MailChecks.Check((await f.Auto.Select(p)).Items.Count==0,"Low-water stop ignored");
        Console.WriteLine("PASS separate trigger and target accounts, subtree/fixed/saved search, age/regex, retained labels/stars, byte budget, keep-newest, low water and change recheck");
    }
}
