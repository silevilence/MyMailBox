using System.Globalization;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailAutomationService
{
    private readonly Func<IReadOnlyList<AutomationExtension>> _extensions;
    public IReadOnlyList<AutomationDescriptor> Extensions=>_extensions().Select(x=>x.Descriptor).ToArray();
    private sealed class ExtensionPaused(string message):Exception(message);
    private AutomationExtension Provider(AutomationBinding binding,AutomationPart part)
    {
        var ext=_extensions().FirstOrDefault(e=>e.Descriptor.Id==binding.Id && e.Descriptor.Part==part)??throw new ExtensionPaused("扩展已停用："+binding.Id+"。配置已保留。");
        if(ext.Descriptor.Version<binding.Version)throw new ExtensionPaused("扩展版本已回退，当前参数需要更高版本："+binding.Id);
        return ext;
    }
    private static async Task<AutomationBinding> Normalize(AutomationBinding b,AutomationExtension provider,CancellationToken token)
    {
        var d=provider.Descriptor;Dictionary<string,string> parameters;
        if(b.Version==d.Version)parameters=new(b.Parameters);
        else
        {
            try{parameters=await provider.Migrate(b.Version,b.Parameters,token);}
            catch(Exception e)when(e is not OperationCanceledException){throw new ExtensionPaused("参数迁移失败，保留原配置："+e.Message);}
        }
        if(parameters.Keys.Any(k=>d.Parameters.All(p=>p.Id!=k)))throw new InvalidDataException("扩展参数包含未声明字段。");
        foreach(var p in d.Parameters)
        {
            if(!parameters.TryGetValue(p.Id,out var value))parameters[p.Id]=value=p.DefaultValue;
            if(p.Required && string.IsNullOrWhiteSpace(value)||p.Number&&!decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out _))
                throw new InvalidDataException("扩展参数无效："+p.Name);
        }
        return new(b.Id,d.Version,parameters);
    }
    private async Task<AutomationPlan> NormalizeExtensions(AutomationPlan p,CancellationToken token)
    {
        async Task<AutomationBinding?> Bind(AutomationBinding? b,AutomationPart part)=>b is null?null:await Normalize(b,Provider(b,part),token);
        var actions=new List<TaskAction>();foreach(var a in p.Actions)
        {
            if(a.Kind==TaskActionKind.Extension&&a.Extension is null)throw new InvalidDataException("请选择动作扩展。");
            actions.Add(a.Kind==TaskActionKind.Extension?a with{Extension=await Bind(a.Extension,AutomationPart.Action)}:a);
        }
        return p with{PluginCondition=await Bind(p.PluginCondition,AutomationPart.Condition),PluginSchedule=await Bind(p.PluginSchedule,AutomationPart.Schedule),Actions=actions.ToArray()};
    }
    private async Task<AutomationAnswer> EvaluatePart(AutomationBinding b,AutomationPart part,Guid? account,DateTimeOffset at,IReadOnlyList<MetricValue> values,bool preview,CancellationToken token)
    {
        if(account is null)throw new InvalidDataException("自动化扩展需要指定授权账户。");
        var provider=Provider(b,part);var parameters=await Normalize(b,provider,token);
        AutomationAnswer answer;
        try{answer=await provider.Evaluate(new(account.Value,at,values.ToDictionary(v=>v.Id,v=>v.Value),preview),parameters.Parameters,token);}
        catch(Exception e)when(e is UnauthorizedAccessException or InvalidOperationException || e is OperationCanceledException && !token.IsCancellationRequested)
        {throw new ExtensionPaused("扩展停用或权限不足，已暂停："+e.Message);}
        return answer.Error switch
        {
            AutomationFailure.None=>answer,AutomationFailure.Unavailable or AutomationFailure.PermissionDenied=>throw new ExtensionPaused(answer.Detail),
            AutomationFailure.Temporary=>throw new IOException(answer.Detail),_=>throw new InvalidDataException(answer.Detail)
        };
    }
    private async Task CheckExtensions(AutomationPlan p,DateTimeOffset at,CancellationToken token)
    {
        if(p.PluginCondition is not null)_=Provider(p.PluginCondition,AutomationPart.Condition);
        if(p.PluginSchedule is {} s)await EvaluatePart(s,AutomationPart.Schedule,p.When.Account,at,[],true,token);
        foreach(var a in p.Actions)if(a.Extension is {} b)await EvaluatePart(b,AutomationPart.Action,p.Target?.Account,at,[],true,token);
    }
    private void PauseRun(Guid run,string reason)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="UPDATE automation_run SET state='paused',detail=$reason WHERE id=$id AND state IN ('running','ready')";
        c.Parameters.AddWithValue("$reason",reason);c.Parameters.AddWithValue("$id",run.ToString());c.ExecuteNonQuery();
    }
}
