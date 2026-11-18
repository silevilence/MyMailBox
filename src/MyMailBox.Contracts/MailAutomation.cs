namespace MyMailBox.Contracts;

public sealed record AutomationCondition(Guid? Account,Guid? Folder,string Filter,string Expression,decimal Hysteresis,int CooldownMinutes);
public sealed record AutomationPlan(Guid Id,int Version,string Name,AutomationCondition When,bool Enabled);
public sealed record MetricValue(string Id,string Name,decimal? Value,string Basis,DateTimeOffset At);
public sealed record ConditionReport(ConditionResult Result,IReadOnlyList<MetricValue> Values,bool Crossed,string Detail,DateTimeOffset At);
public interface IMailAutomation
{
    Task<IReadOnlyList<AutomationPlan>> Plans(CancellationToken token=default);
    Task<AutomationPlan> Save(AutomationPlan plan,CancellationToken token=default);
    Task<ConditionReport> Inspect(AutomationPlan plan,bool observe=false,CancellationToken token=default);
    Task<SearchLibrary> Library(CancellationToken token=default);
}
