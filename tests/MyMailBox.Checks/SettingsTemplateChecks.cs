using System.Text;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static class SettingsTemplateChecks
{
    private sealed class Workspace
    {
        public SqliteMailStore Store {get;}
        public AccountService Accounts {get;}
        public ProtocolMailService Protocols {get;}
        public MailSearchService Search {get;}
        public ReceiveRuleService Rules {get;}
        public MailAutomationService Automation {get;}
        public SettingsTemplates Templates {get;}
        public PluginConfiguration Config = new(1,[]);
        public Workspace(string root)
        {
            Store=new(root); Accounts=new(Store); Protocols=new(Store); Search=new(Store,Protocols); Rules=new(Store,Search,Protocols); Automation=new(Store,Search,Protocols);
            Templates=new(Accounts,Protocols,Search,Rules,Automation,()=>Config,c=>{Config=c;return Task.CompletedTask;},()=>[PluginChecks.Example()]);
        }
    }
    internal static async Task Run()
    {
        var root=Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!,"settings-template");
        var source=new Workspace(Path.Combine(root,"source"));var dest=new Workspace(Path.Combine(root,"destination"));
        var a=await source.Accounts.Save(new(null,"同名邮箱","local",Alias:"work"));
        var b=await dest.Accounts.Save(new(null,"同名邮箱","local",Alias:"work"));var other=await dest.Accounts.Save(new(null,"同名邮箱","local"));
        var sensitive="fixture-not-for-export";
        var smtp=await source.Accounts.Save(new(null,"样本","smtp","fixture@example.invalid",Notes:sensitive),sensitive);
        await source.Protocols.Save(smtp.Id,new(Outgoing:new("smtp.example.invalid",465,smtp.Address)),null,sensitive);
        var srcFolder=(await source.Store.Folders()).Single(f=>f.AccountId==a.Id).Id;
        var dstFolder=(await dest.Store.Folders()).Single(f=>f.AccountId==b.Id).Id;
        var wrongFolder=(await dest.Store.Folders()).Single(f=>f.AccountId==other.Id).Id;
        await Add(source,srcFolder); await Add(dest,dstFolder);
        var rule=await source.Rules.Save(new(Guid.NewGuid(),0,"模板规则",a.Id,srcFolder,"subject:Order AND accountid=\""+a.Id+"\"","subject:skip",[new(RuleActionKind.Read)],8,true,true,false));
        var plan=await source.Automation.Save(new(Guid.NewGuid(),0,"模板任务",new(a.Id,srcFolder,"subject:Order","count>=1",0,3),true));
        source.Config=new(1,[new("example-one","example","1.0.0",true,new(){Greeting="模板问候"},["mail.read:"+a.Id])]);
        var file=Path.Combine(root,"settings.json");
        await source.Templates.Export(file,default);
        var text=await File.ReadAllTextAsync(file);
        PluginChecks.Check(!text.Contains(sensitive) && !text.Contains(smtp.CredentialReference!),"Template leaked secret/reference");
        var opened=await dest.Templates.Open(file,default);var digest=opened.Digest;
        var ruleKey="rule/"+rule.Id;
        var fields=await dest.Templates.Fields(file,digest,ruleKey,default);
        var accountKey="account/"+a.Id;var folderKey="folder/"+srcFolder;
        PluginChecks.Check(fields.Single(f=>f.Key==accountKey).Choices!.Count(c=>c.Label.Contains("同名邮箱"))==2,"Same-name accounts collapsed");
        var map=new Dictionary<string,string>{{accountKey,b.Id.ToString()},{folderKey,dstFolder.ToString()}};
        await PluginChecks.Reject(()=>dest.Templates.Review(file,digest,ruleKey,new Dictionary<string,string>(),true,default),"请映射");
        var wrong=new Dictionary<string,string>(map){[folderKey]=wrongFolder.ToString()};
        await PluginChecks.Reject(()=>dest.Templates.Review(file,digest,ruleKey,wrong,true,default),"请映射");
        var detail=await dest.Templates.Review(file,digest,ruleKey,map,false,default);
        PluginChecks.Check(detail.Contains("命中 1 封") && !(await dest.Rules.Dashboard()).Rules.Any(),"Preview wrote state or changed meaning");
        await dest.Templates.Review(file,digest,ruleKey,map,true,default);
        var imported=(await dest.Rules.Dashboard()).Rules.Single();
        PluginChecks.Check(!imported.Enabled && imported.Stop && imported.Order==8 && imported.Account==b.Id && imported.Folder==dstFolder
            && imported.Condition.Contains(b.Id.ToString()) && !imported.Condition.Contains(a.Id.ToString()),"Rule retained source references");
        await dest.Templates.Review(file,digest,"plan/"+plan.Id,map,true,default);
        var task=(await dest.Automation.Plans()).Single();
        PluginChecks.Check(!task.Enabled && task.When.Account==b.Id && task.When.Folder==dstFolder && task.Approval==null,"Task activated or retained source scope");
        var accountMapping=new Dictionary<string,string>{{"account-mode","new"},{"alias","work"}};
        await PluginChecks.Reject(()=>dest.Templates.Review(file,digest,accountKey,accountMapping,true,default),"alias");
        accountMapping["alias"]="work_copy";await dest.Templates.Review(file,digest,accountKey,accountMapping,true,default);
        await dest.Templates.Review(file,digest,"account/"+smtp.Id,new Dictionary<string,string>{{"account-mode","new"}},true,default);
        var restored=(await dest.Accounts.List()).Single(x=>x.Provider=="smtp");
        PluginChecks.Check(restored.CredentialReference==null && (await dest.Protocols.Settings(restored.Id)).Outgoing?.Host=="smtp.example.invalid","Failed to restore endpoint without credential");
        var plugin=new Dictionary<string,string>{{"instance","example-copy"}};
        await dest.Templates.Review(file,digest,"plugin/example-one",plugin,true,default);
        PluginChecks.Check(dest.Config.Instances.Single() is {Enabled:false,Grants.Length:0,Version:"1.0.0",Options.Greeting:"模板问候"},"Plugin gained source privileges");
        await File.WriteAllTextAsync(file,text.Replace("smtp.example.invalid","smtp2.example.invalid"));
        await PluginChecks.Reject(()=>dest.Templates.Review(file,digest,ruleKey,map,true,default),"校验失败");
        PluginChecks.Check((await dest.Rules.Dashboard()).Rules.Count==1 && (await dest.Automation.Plans()).Count==1,"Damaged file changed state");
        Console.WriteLine("PASS settings templates: secret-free profiles, integrity, explicit same-name bindings, alias/foreign-folder refusal, equivalent preview, disabled tasks and zero-grant plugin restore");
    }
    private static async Task Add(Workspace w,Guid folder)
    {
        var raw=Encoding.UTF8.GetBytes("From: test@example.invalid\r\nTo: inbox@example.invalid\r\nSubject: Order fixture\r\n\r\nexample");
        await w.Store.Import(folder,raw,await new MimeReader().Parse(raw));
    }
}
