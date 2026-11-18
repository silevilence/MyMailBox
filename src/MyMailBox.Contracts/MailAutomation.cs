namespace MyMailBox.Contracts;

public sealed record AutomationCondition(Guid? Account,Guid? Folder,string Filter,string Expression,decimal Hysteresis,int CooldownMinutes);
public sealed record TargetSelection(Guid? Account,Guid? Folder,bool Descendants,string Filter,Guid? SavedSearch,string[]? Fixed,
    int AgeDays,int KeepNewest,bool KeepStarred,string KeepLabel,SearchOrder Order,int MaxCount,long MaxBytes,long? LowWaterBytes);
public sealed record TargetMail(string Id,Guid Account,Guid Folder,string Key,string Subject,DateTimeOffset? Sent,long Size,uint? Validity,string? Hash,string Stamp);
public sealed record TargetSnapshot(Guid Id,DateTimeOffset At,string TriggerScope,string TargetScope,IReadOnlyList<TargetMail> Items,long ScopeBytes,long SelectedBytes,string Detail);
public sealed record AutomationPlan(Guid Id,int Version,string Name,AutomationCondition When,bool Enabled)
{
    public TargetSelection? Target {get;init;}
}
public sealed record MetricValue(string Id,string Name,decimal? Value,string Basis,DateTimeOffset At);
public sealed record ConditionReport(ConditionResult Result,IReadOnlyList<MetricValue> Values,bool Crossed,string Detail,DateTimeOffset At);
public interface IMailAutomation
{
    Task<IReadOnlyList<AutomationPlan>> Plans(CancellationToken token=default);
    Task<AutomationPlan> Save(AutomationPlan plan,CancellationToken token=default);
    Task<ConditionReport> Inspect(AutomationPlan plan,bool observe=false,CancellationToken token=default);
    Task<TargetSnapshot> Select(AutomationPlan plan,DateTimeOffset? at=null,CancellationToken token=default);
    Task<string?> Recheck(AutomationPlan plan,TargetSnapshot snapshot,TargetMail mail,CancellationToken token=default);
    Task<SearchLibrary> Library(CancellationToken token=default);
}
