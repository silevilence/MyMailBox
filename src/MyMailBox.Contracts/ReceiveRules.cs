namespace MyMailBox.Contracts;

public enum RuleActionKind { Read, Unread, Star, Unstar, Label, Move }
public sealed record RuleAction(RuleActionKind Kind, string? Label = null, Guid? Destination = null);
public sealed record ReceiveRule(Guid Id, int Version, string Name, Guid? Account, Guid? Folder,
    string Condition, string Exception, RuleAction[] Actions, int Order, bool Enabled, bool Stop, bool IncludeHistory);
public sealed record RuleRun(string Id, string Event, Guid Rule, int Version, string Name, string State, string Detail);
public sealed record RuleDashboard(IReadOnlyList<ReceiveRule> Rules, IReadOnlyList<RuleRun> Runs, IReadOnlyList<SearchScope> Scopes);
public interface IReceiveRules
{
    Task<RuleDashboard> Dashboard(CancellationToken token = default);
    Task<ReceiveRule> Save(ReceiveRule rule, CancellationToken token = default);
    Task<int> Process(int limit = 32, CancellationToken token = default);
}
