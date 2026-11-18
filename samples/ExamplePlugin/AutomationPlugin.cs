using CordiSharp;
using CordiSharp.Registry;
using MyMailBox.Contracts;
using System.Globalization;

namespace ExamplePlugin;

[Plugin("automation-example")]
[Inject("mailbox.host")]
public sealed class AutomationExample:IPlugin<PluginOptions>
{
#if PLUGIN_V2
    private const int Schema=2;
    private const string LabelKey="label";
#else
    private const int Schema=1;
    private const string LabelKey="tag";
#endif
    public void Load(Context context,PluginOptions options)
    {
        var host=context.Get<IPluginHost>("mailbox.host")!;
        void Add(string id,string name,AutomationPart part,AutomationParameter parameter,Func<AutomationInput,IReadOnlyDictionary<string,string>,AutomationAnswer> evaluate)
        {
            context.Effect(()=>host.AddAutomationExtension(new(new(id,name,part,Schema,[parameter],"automation."+part.ToString().ToLowerInvariant()),
                (input,p,t)=>{t.ThrowIfCancellationRequested();return Task.FromResult(evaluate(input,p));},
                (version,old,t)=>
                {
                    t.ThrowIfCancellationRequested();if(version!=1||Schema!=2)throw new InvalidDataException("不支持此参数版本。");
                    var migrated=old.ToDictionary();if(part==AutomationPart.Action && migrated.Remove("tag",out var tag))migrated["label"]=tag;
                    return Task.FromResult(migrated);
                })));
        }
        Add("minimum","未读数量下限",AutomationPart.Condition,new("minimum","最少未读封数",true,"1"),(input,p)=>
        {
            var n=decimal.Parse(p["minimum"],CultureInfo.InvariantCulture);var unread=input.Metrics.GetValueOrDefault("unread");
            return new("读取本次未读采样。",Condition:unread.HasValue?(unread>=n?ConditionResult.True:ConditionResult.False):ConditionResult.Unknown);
        });
        Add("category","添加归类标签",AutomationPart.Action,new(LabelKey,"标签",false,"待跟进"),(_,p)=>
            new("添加本地分类："+p[LabelKey],Effect:new(TaskActionKind.Label,Argument:p[LabelKey])));
        Add("utc-daily","按 UTC 整点检查",AutomationPart.Schedule,new("hour","UTC 小时",true,"0"),(input,p)=>
        {
            if(!int.TryParse(p["hour"],out int hour)||hour is <0 or >23)return new("UTC 小时须在 0–23 之间。",AutomationFailure.InvalidParameter);
            return new("按 UTC 日期去重。",Occurrence:input.At.UtcDateTime.Hour>=hour?input.At.UtcDateTime.ToString("yyyyMMdd"):null);
        });
    }
}
