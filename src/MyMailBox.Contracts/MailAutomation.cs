namespace MyMailBox.Contracts;

public sealed record AutomationCondition(Guid? Account,Guid? Folder,string Filter,string Expression,decimal Hysteresis,int CooldownMinutes);
public sealed record TargetSelection(Guid? Account,Guid? Folder,bool Descendants,string Filter,Guid? SavedSearch,string[]? Fixed,
    int AgeDays,int KeepNewest,bool KeepStarred,string KeepLabel,SearchOrder Order,int MaxCount,long MaxBytes,long? LowWaterBytes);
public sealed record TargetMail(string Id,Guid Account,Guid Folder,string Key,string Subject,DateTimeOffset? Sent,long Size,uint? Validity,string? Hash,string Stamp);
public sealed record TargetSnapshot(Guid Id,DateTimeOffset At,string TriggerScope,string TargetScope,IReadOnlyList<TargetMail> Items,long ScopeBytes,long SelectedBytes,string Detail);
public enum TaskActionKind { Copy, Read, Unread, Star, Unstar, Label, SaveAttachments, ExportEml, Notify, Trash, DeletePermanently }
public enum StepFailure { Stop, Continue, Retry }
public sealed record TaskAction(TaskActionKind Kind,Guid? Destination=null,string Argument="",StepFailure Failure=StepFailure.Stop,int Retries=0);
public sealed record ActionPreview(string[] Effects,int Selected,long Bytes,string Approval);
public sealed record TaskStepLog(int Item,int Step,TaskActionKind Kind,string State,int Attempts,string Detail);
public sealed record ArchivePlan(Guid Account,Guid Folder,bool Daily,int LocalMinute,string Zone);
public sealed record AutomationRun(Guid Id,Guid Plan,string Cause,string State,string Detail,int Done,int Skipped,int Failed,DateTimeOffset At);
public sealed record AutomationPlan(Guid Id,int Version,string Name,AutomationCondition When,bool Enabled)
{
    public TaskAction[] Actions {get;init;}=[];
    public string? Approval {get;init;}
    public ArchivePlan? Archive {get;init;}
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
    Task<ActionPreview> PreviewActions(AutomationPlan plan,CancellationToken token=default);
    Task<IReadOnlyList<TaskStepLog>> Steps(Guid run,CancellationToken token=default);
    Task<AutomationRun> Start(Guid plan,string cause,CancellationToken token=default);
    Task<IReadOnlyList<AutomationRun>> Runs(Guid plan,CancellationToken token=default);
    Task<AutomationRun> Advance(Guid run,CancellationToken token=default);
    Task Control(Guid run,bool resume,CancellationToken token=default);
    Task<IReadOnlyList<MailFolder>> ArchiveFolders(CancellationToken token=default);
    Task<SearchLibrary> Library(CancellationToken token=default);
}
