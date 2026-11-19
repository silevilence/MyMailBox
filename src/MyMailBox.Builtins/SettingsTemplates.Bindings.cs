using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public sealed partial class SettingsTemplates
{
    private sealed class Bindings(Bundle source,IReadOnlyDictionary<string,string>? values,IReadOnlyList<MailAccount> accounts,
        SearchLibrary library,IReadOnlyList<AutomationPlan> plans,PluginConfiguration config,IReadOnlyList<AutomationDescriptor> extensions)
    {
        internal IReadOnlyDictionary<string,string>? Values => values;
        internal IReadOnlyList<MailAccount> Accounts => accounts;
        internal SearchLibrary Library => library;
        internal Dictionary<string,TemplateField> Fields { get; } = new(StringComparer.Ordinal);
        internal bool Collecting => values == null;
        internal string Choice(string key,string label,Choice[] choices)
        {
            Fields.TryAdd(key,new(key,label,choices));
            if (values == null) return "";
            if (!values.TryGetValue(key,out var value) || !choices.Any(c=>c.Id==value)) throw new InvalidDataException("请映射："+label);
            return value;
        }
        internal string Text(string key,string label,string initial="",bool optional=false)
        {
            Fields.TryAdd(key,new(key,label,null,initial));
            if (values == null) return initial;
            var value=values.GetValueOrDefault(key,initial);
            if (!optional && string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("请填写："+label);
            return value;
        }
        internal Guid? Account(Guid? id)
        {
            if(id==null) return null;
            var src=source.Accounts.FirstOrDefault(a=>a.Settings.Id==id)?.Settings;
            var choices=accounts.Where(a=>a.Enabled).Select(a=>new Choice(a.Id.ToString(),a.Name+" · "+a.Address+" · "+a.Id)).ToArray();
            var selected=Choice("account/"+id,$"账户 {src?.Name} · {src?.Address} · {id}",choices);
            return Collecting ? id : Guid.Parse(selected);
        }
        internal Guid? Folder(Guid? id)
        {
            if(id==null) return null;
            var src=source.Scopes.SingleOrDefault(s=>s.Folder==id);
            var parent=Account(src?.Account);
            var choices=library.Scopes.Where(s=>s.Folder!=null && (Collecting || parent==null || s.Account==parent))
                .Select(s=>new Choice(s.Folder.ToString()!,s.Name+" · "+s.Folder)).ToArray();
            var selected=Choice("folder/"+id,$"目录 {src?.Name} · {id}",choices);
            return Collecting ? id : Guid.Parse(selected);
        }
        internal string Provider(string id)
        {
            if(!id.Contains('/')) return id;
            var parts=id.Split('/',2);
            return Instance(parts[0])+"/"+parts[1];
        }
        private string Instance(string instance)
        {
            var p=source.Plugins.SingleOrDefault(p=>p.Instance==instance);
            var choices=config.Instances.Where(i=>i.Enabled && i.Plugin==p?.Package && i.Version==p.Version)
                .Select(i=>new Choice(i.Id,i.Id+" · "+i.Plugin+" "+i.Version)).ToArray();
            var selected=Choice("plugin/"+instance,$"启用的插件 {p?.Package ?? instance} {p?.Version}（来源 {instance}）",choices);
            return Collecting ? instance : selected;
        }
        private void Grant(string instance,string capability,Guid? account)
        {
            if(Collecting) return;
            var grants=config.Instances.Single(p=>p.Id==instance).Grants ?? [];
            if(!grants.Contains(capability) && (account==null || !grants.Contains(capability+":"+account)))
                throw new InvalidDataException($"请先在插件管理核对此实例的授权：{instance} · {capability} · {account}。");
        }
        internal string Expression(string expression,Guid? account=null)
        {
            if(string.IsNullOrWhiteSpace(expression)) return expression;
            return ConditionEditor.Expression(Visit(ConditionTree.Parse(expression).Root));
            ConditionNode Visit(ConditionNode n)
            {
                if(n.Criterion is not {} c) return n with { Children=n.Children.Select(Visit).ToArray() };
                if(c.Field is "accountid" or "folderid" && c.Match==SearchMatch.Equals && Guid.TryParse(c.Value,out var id))
                    return n with{ Criterion=c with { Value=(c.Field=="accountid"?Account(id):Folder(id)).ToString()! } };
                if(c.Field is "account" or "folder" or "accountid" or "folderid")
                {
                    var key="filter/"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Field+"/"+c.Value)))[..16];
                    var value=Text(key,"确认目标环境匹配值："+c.Field+" "+c.Value);
                    return n with{ Criterion=c with{ Value=value } };
                }
                if(c.Field.StartsWith("plugin.",StringComparison.Ordinal))
                {
                    var plugin=source.Plugins.Where(p=>c.Field.StartsWith("plugin."+p.Instance+".",StringComparison.Ordinal)).MaxBy(p=>p.Instance.Length);
                    if(plugin==null) throw new InvalidDataException("无法识别扩展搜索字段："+c.Field);
                    string instance=Instance(plugin.Instance);
                    Grant(instance,"mail.read",account);
                    return n with { Criterion=c with { Field="plugin."+instance+c.Field[(7+plugin.Instance.Length)..] } };
                }
                return n;
            }
        }
        private AutomationBinding? Extension(AutomationBinding? old,AutomationPart part,Guid? account,string position)
        {
            if(old==null) return null;
            var split=old.Id.Split('/',2);
            if(split.Length!=2) throw new InvalidDataException("扩展标识无效："+old.Id);
            var instance=Instance(split[0]); var id=instance+"/"+split[1];
            if(!Collecting)
            {
                var d=extensions.SingleOrDefault(d=>d.Id==id && d.Part==part && d.Version==old.Version) ?? throw new InvalidDataException("缺少目标扩展或参数版本："+id);
                Grant(instance,d.Capability,account);
                if(part==AutomationPart.Action) Grant(instance,"mail.write",account);
            }
            return old with { Id=id, Parameters=old.Parameters.Keys.ToDictionary(k=>k,k=>Text(position+"/"+k,"重新填写扩展参数 "+id+" · "+k)) };
        }
        internal AutomationPlan Plan(AutomationPlan p,Guid id)
        {
            var account=Account(p.When.Account);
            var when=p.When with { Account=account,Folder=Folder(p.When.Folder),Filter=Expression(p.When.Filter,account) };
            var target=p.Target;
            if(target != null)
            {
                var targetAccount=Account(target.Account);
                var saved=target.SavedSearch;
                if(saved!=null)
                {
                    var choice=Choice("search/"+saved,"目标搜索："+source.Searches.FirstOrDefault(s=>s.Id==saved)?.Name,
                        library.Saved.Select(s=>new Choice(s.Id.ToString(),s.Name+" · "+s.Id)).ToArray());
                    if(!Collecting) saved=Guid.Parse(choice);
                }
                target=target with{ Account=targetAccount,Folder=Folder(target.Folder),SavedSearch=saved,Filter=Expression(target.Filter,targetAccount),
                    Fixed=target.Fixed?.Select((key,i)=>Text("mail/"+i,"固定邮件 "+key+" 对应的本机邮件标识")).ToArray() };
            }
            var actions=p.Actions.Select((a,i)=>a with { Destination=Folder(a.Destination),
                Argument=NeedsArgument(a.Kind)?Text("argument/"+i,"重新填写 "+a.Kind+" 的本机参数"):a.Argument,
                Extension=Extension(a.Extension,AutomationPart.Action,target?.Account,"action/"+i) }).ToArray();
            var triggers=p.Triggers.Select(t=>
            {
                if(t.Source is not {} s) return t;
                if(t.Kind!="task") return t with { Source=Account(s) };
                var selected=Choice("plan/"+s,"依赖任务 "+source.Plans.FirstOrDefault(p=>p.Id==s)?.Name+" · "+s,
                    plans.Select(p=>new Choice(p.Id.ToString(),p.Name+" · "+p.Id)).ToArray());
                return t with{Source=Collecting?s:Guid.Parse(selected)};
            }).ToArray();
            return p with{ Id=id,Version=0,Enabled=false,When=when,Target=target,Actions=actions,Triggers=triggers,Approval=null,
                PluginCondition=Extension(p.PluginCondition,AutomationPart.Condition,account,"condition"),
                PluginSchedule=Extension(p.PluginSchedule,AutomationPart.Schedule,account,"schedule"),
                Archive=p.Archive is {} a?a with{Account=Account(a.Account)!.Value,Folder=Folder(a.Folder)!.Value}:null };
        }
    }
}
