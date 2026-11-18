namespace MyMailBox.Contracts;

public enum AutomationPart { Condition, Action, Schedule }
public enum AutomationFailure { None, InvalidParameter, Unavailable, PermissionDenied, Temporary }
public sealed record AutomationParameter(string Id,string Name,bool Number,string DefaultValue,bool Required=true);
public sealed record AutomationDescriptor(string Id,string Name,AutomationPart Part,int Version,AutomationParameter[] Parameters,string Capability);
public sealed record AutomationBinding(string Id,int Version,Dictionary<string,string> Parameters);
public sealed record AutomationInput(Guid Account,DateTimeOffset At,IReadOnlyDictionary<string,decimal?> Metrics,bool Preview);
public sealed record AutomationAnswer(string Detail,AutomationFailure Error=AutomationFailure.None,ConditionResult? Condition=null,TaskAction? Effect=null,string? Occurrence=null);
public sealed record AutomationExtension(AutomationDescriptor Descriptor,
    Func<AutomationInput,IReadOnlyDictionary<string,string>,CancellationToken,Task<AutomationAnswer>> Evaluate,
    Func<int,IReadOnlyDictionary<string,string>,CancellationToken,Task<Dictionary<string,string>>> Migrate);
