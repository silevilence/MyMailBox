using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public sealed partial class SettingsTemplates(AccountService accounts,ProtocolMailService protocols,IMailSearch search,
    IReceiveRules rules,IMailAutomation automation,Func<PluginConfiguration> configuration,Func<PluginConfiguration,Task> saveConfiguration,
    Func<IReadOnlyCollection<PluginDefinition>> packages) : ISettingsTemplates
{
    private sealed record AccountTemplate(AccountEdit Settings,ProtocolSettings? Connection);
    private sealed record PluginTemplate(string Instance,string Package,string Version,string Greeting,string[] RequiredGrants);
    private sealed record Bundle(int Schema,Guid Id,AccountTemplate[] Accounts,SearchScope[] Scopes,ReceiveRule[] Rules,
        AutomationPlan[] Plans,SavedSearch[] Searches,PluginTemplate[] Plugins);
    private sealed record Document(string Sha256,JsonElement Payload);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task Export(string file,CancellationToken token)
    {
        var library = await search.Library(token);
        var accountTemplates = new List<AccountTemplate>();
        foreach (var a in await accounts.List(token))
        {
            ProtocolSettings? connection = null;
            if (a.Enabled && a.Provider is "imap" or "imap-smtp" or "smtp" or "pop3") connection = await protocols.Settings(a.Id,token);
            accountTemplates.Add(new(new(a.Id,a.Name,a.Provider,a.Address,a.Alias,a.Tags,a.Color,Order:a.Order,Capabilities:a.Capabilities),connection));
        }
        var definitions = (await rules.Dashboard(token)).Rules.Select(r=>r with { Enabled=false,MailApproval=null,
            Actions=r.Actions.Select(a=>a with{MailArgument=null}).ToArray() }).ToArray();
        var plans = (await automation.Plans(token)).Select(p=>p with {
            Enabled=false,Approval=null,PluginSchedule=Portable(p.PluginSchedule),PluginCondition=Portable(p.PluginCondition),
            Actions=p.Actions.Select(a=>a with { Extension=Portable(a.Extension),Argument=NeedsArgument(a.Kind)?"":a.Argument }).ToArray() }).ToArray();
        var plugins = configuration().Instances.Select(p=>new PluginTemplate(p.Id,p.Plugin,p.Version,p.Options?.Greeting ?? "你好",p.Grants ?? [])).ToArray();
        var bundle = new Bundle(1,Guid.NewGuid(),accountTemplates.ToArray(),library.Scopes.ToArray(),definitions,plans,library.Saved.ToArray(),plugins);
        var data = JsonSerializer.SerializeToUtf8Bytes(bundle,Json);
        using var json = JsonDocument.Parse(data);
        var doc = new Document(Convert.ToHexString(SHA256.HashData(data)),json.RootElement);
        var output = Path.GetFullPath(file); var staging = output+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await File.WriteAllBytesAsync(staging,JsonSerializer.SerializeToUtf8Bytes(doc,Json),token);
            File.Move(staging,output);
        }
        finally { if(File.Exists(staging)) File.Delete(staging); }
    }

    private static bool NeedsArgument(TaskActionKind kind) => kind is TaskActionKind.AutoReply or TaskActionKind.AutoForward or TaskActionKind.SaveAttachments or TaskActionKind.ExportEml;
    private static AutomationBinding? Portable(AutomationBinding? b) => b == null ? null :
        b with { Parameters=b.Parameters.Keys.ToDictionary(k=>k,_=>"") };
    private static IEnumerable<TemplateEntry> Entries(Bundle b) =>
        b.Accounts.Select(a=>new TemplateEntry("account/"+a.Settings.Id,"账户 · "+a.Settings.Name))
         .Concat(b.Searches.Select(s=>new TemplateEntry("search/"+s.Id,"保存的搜索 · "+s.Name)))
         .Concat(b.Rules.Select(r=>new TemplateEntry("rule/"+r.Id,"规则 · "+r.Name)))
         .Concat(b.Plans.Select(p=>new TemplateEntry("plan/"+p.Id,"任务 · "+p.Name)))
         .Concat(b.Plugins.Select(p=>new TemplateEntry("plugin/"+p.Instance,"普通插件 · "+p.Instance+" · "+p.Package+" "+p.Version)));
    private static async Task<(Bundle Bundle,string Digest)> Read(string file,string? expected,CancellationToken token)
    {
        using var source = File.OpenRead(file);
        if (source.Length is 0 or > 16*1024*1024) throw new InvalidDataException("配置模板为空或超过 16 MiB。");
        try
        {
            var doc = await JsonSerializer.DeserializeAsync<Document>(source,Json,token) ?? throw new InvalidDataException("模板为空。");
            var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(doc.Payload.GetRawText())));
            if (digest != doc.Sha256 || expected != null && expected != digest) throw new InvalidDataException("模板内容已变化或校验失败，请重新打开。");
            var b = doc.Payload.Deserialize<Bundle>(Json);
            if (b == null || b.Schema != 1 || b.Id == Guid.Empty || b.Accounts.Any(a=>a?.Settings?.Id is null) ||
                b.Rules.Any(r=>r == null || r.Enabled || r.Actions == null) || b.Plans.Any(p=>p == null || p.Enabled || p.When == null || p.Actions == null) ||
                b.Searches.Any(s=>s == null) || b.Scopes.Any(s=>s==null) || b.Plugins.Any(p=>p==null || !Version.TryParse(p.Version,out _)))
                throw new InvalidDataException("模板版本或条目无效。");
            var keys = Entries(b).Select(e=>e.Key).ToArray();
            if (keys.Length != keys.Distinct().Count()) throw new InvalidDataException("模板内存在重复标识。");
            return (b,digest);
        }
        catch(JsonException e) { throw new InvalidDataException("配置模板格式损坏。",e); }
    }
    public async Task<TemplateCatalog> Open(string file,CancellationToken token)
    {
        var (b,digest) = await Read(file,null,token); return new(digest,Entries(b).ToArray());
    }

    public async Task<TemplateField[]> Fields(string file,string digest,string entry,CancellationToken token)
    {
        var (b,_) = await Read(file,digest,token);
        var mapper = await Mapper(b,null,token);
        await Resolve(b,entry,mapper,false,token);
        return mapper.Fields.Values.ToArray();
    }

    public async Task<string> Review(string file,string digest,string entry,IReadOnlyDictionary<string,string> bindings,bool save,CancellationToken token)
    {
        var (b,_) = await Read(file,digest,token);
        var mapper = await Mapper(b,bindings,token);
        return await Resolve(b,entry,mapper,save,token);
    }
    private async Task<Bindings> Mapper(Bundle bundle,IReadOnlyDictionary<string,string>? values,CancellationToken token) =>
        new(bundle,values,await accounts.List(token),await search.Library(token),await automation.Plans(token),configuration(),automation.Extensions);
    private async Task<string> Resolve(Bundle b,string entry,Bindings m,bool save,CancellationToken t)
    {
        if (!Entries(b).Any(e=>e.Key==entry)) throw new InvalidDataException("模板条目不存在。");
        var (kind,key)=(entry[..entry.IndexOf('/')],entry[(entry.IndexOf('/')+1)..]);
        var newId = new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { b.Id,entry, mapping=m.Values?.OrderBy(k=>k.Key,StringComparer.Ordinal).ToArray() })).AsSpan(0,16));
        if (kind=="plugin") return await Plugin(b.Plugins.Single(p=>p.Instance==key),m,save);
        var id = Guid.Parse(key);
        if (kind=="account") return await Account(b.Accounts.Single(a=>a.Settings.Id==id),m,save,t);
        if (kind=="search")
        {
            var src=b.Searches.Single(s=>s.Id==id);
            var mapped=src with { Id=newId,Account=m.Account(src.Account),Folder=m.Folder(src.Folder),Expression=m.Expression(src.Expression,m.Account(src.Account)) };
            if (m.Collecting) return "";
            if (!mapped.HasScope(m.Library)) throw new InvalidDataException("请选择同一账户的目录。");
            var result=await search.Search(new([]){Condition=mapped.Condition()},t);
            if(result.UnavailableFields.Count!=0) throw new InvalidDataException("搜索扩展不可用："+string.Join("、",result.UnavailableFields));
            if (save)
            {
                if (m.Library.Saved.Any(s=>s.Id==newId)) throw new InvalidDataException("此搜索映射已导入。");
                await search.Save(mapped,t);
            }
            return $"{mapped.Name} · 本页命中 {result.Items.Count} 封。";
        }
        if (kind=="rule")
        {
            var src=b.Rules.Single(r=>r.Id==id);
            var rule=src with { Id=newId,Version=0,Account=m.Account(src.Account),Folder=m.Folder(src.Folder),
                Condition=m.Expression(src.Condition,m.Account(src.Account)),Exception=m.Expression(src.Exception,m.Account(src.Account)),Enabled=false,MailApproval=null,
                Actions=src.Actions.Select((a,i)=>a with{ Destination=m.Folder(a.Destination),MailArgument=a.Kind is RuleActionKind.AutoReply or RuleActionKind.AutoForward?
                    m.Text("rule-argument-"+i,"重新填写外发动作参数") : null }).ToArray() };
            if (m.Collecting) return "";
            var preview=await rules.Preview(rule,t);
            if (preview.Warnings.Count!=0) throw new InvalidDataException(string.Join(" ",preview.Warnings));
            if(save) await rules.Save(rule,t);
            return $"{rule.Name} · {preview.Scope} · 命中 {preview.Matches} 封，排除 {preview.Exceptions} 封。"+string.Join(" → ",preview.Effects);
        }
        var plan=m.Plan(b.Plans.Single(p=>p.Id==id),newId);
        if(m.Collecting) return "";
        if(plan.Target?.Fixed is {Length:>0} keys)
        {
            var target=plan.Target;
            var scope=new SavedSearch(Guid.Empty,"","",SearchOrder.Newest,target.Account,target.Descendants?null:target.Folder);
            foreach(var mailKey in keys)
                if(await search.Evaluate(mailKey,scope.Condition(),t)!=ConditionResult.True) throw new InvalidDataException("固定邮件在所选本机范围内不可用："+mailKey);
        }
        var condition=await automation.Inspect(plan,false,t);
        var selected=plan.Target == null ? null : await automation.Select(plan,null,t);
        var effects=plan.Actions.Length==0 ? null : await automation.PreviewActions(plan,t);
        if(save) await automation.Save(plan,t);
        return $"{plan.Name} · 条件 {condition.Result}，目标 {selected?.Items.Count ?? 0} 封。"+string.Join(" → ",effects?.Effects ?? []);
    }
    private async Task<string> Account(AccountTemplate template,Bindings m,bool save,CancellationToken t)
    {
        var source=template.Settings;
        var provider=m.Provider(source.Provider);
        var choices=new[]{new Choice("new","新建账户")}.Concat(m.Accounts.Where(a=>a.Provider==provider).Select(a=>new Choice(a.Id.ToString(),a.Name+" · "+a.Address+" · "+a.Id))).ToArray();
        string choice=m.Choice("account-mode","新建或明确关联已有账户",choices);
        string alias=m.Text("alias","新建账户的 alias（可留空）",source.Alias ?? "",true);
        if(m.Collecting) return "";
        if(choice!="new") return "已选择本机账户 "+m.Accounts.Single(a=>a.Id.ToString()==choice).Name+"。已有账户配置保留，可在规则和任务映射时选用。";
        if(alias.Length!=0 && m.Accounts.Any(a=>a.Alias?.Equals(alias,StringComparison.OrdinalIgnoreCase)==true)) throw new InvalidDataException("alias 已被本机账户使用，请填写另一个 alias。");
        var edit=source with { Id=null,Provider=provider,Alias=alias,Enabled=true,Notes="" };
        var detail="新建 "+edit.Name+" · "+edit.Provider+" · "+edit.Address;
        if(save)
        {
            var connection=template.Connection;
            if(connection?.Incoming!=null || connection?.Outgoing!=null)
                await new OutlookAccountImport(accounts).Import(edit,connection!,null,t);
            else await accounts.Save(edit,cancellation:t);
        }
        return detail+"。在线账户需在本机重新授权。";
    }
    private async Task<string> Plugin(PluginTemplate p,Bindings m,bool save)
    {
        string instance=m.Text("instance","本机新实例 ID",p.Instance);
        if(m.Collecting) return "";
        if(!System.Text.RegularExpressions.Regex.IsMatch(instance,@"\A[a-zA-Z0-9_-]{1,64}\z")) throw new InvalidDataException("实例 ID 格式无效。");
        if(!packages().Any(d=>d.Manifest.Id==p.Package && d.Manifest.Version==p.Version && d.Manifest.Kind==PluginKind.Ordinary))
            throw new InvalidDataException($"请先安装 {p.Package} {p.Version}。");
        var config=configuration();
        if(config.Instances.Any(i=>i.Id==instance)) throw new InvalidDataException("实例 ID 已存在，请选择新 ID。");
        if(save) await saveConfiguration(config with { Instances=[..config.Instances,new(instance,p.Package,p.Version,false,new(){Greeting=p.Greeting},[])] });
        return $"{p.Package} {p.Version} · {instance} · 停用。请在插件管理逐项授予所需权限："+string.Join("、",p.RequiredGrants);
    }
}
