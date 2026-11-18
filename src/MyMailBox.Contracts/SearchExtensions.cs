namespace MyMailBox.Contracts;

public enum SearchInput { Subject, From, Body, Headers }
public sealed record SearchCandidate(string Id, Guid Account, string Text);
public sealed record SearchDerived(string Id, string? Value);
public sealed record SearchDecision(string Id, bool? Match, SearchExcerpt? Highlight);

// CandidateContains is a necessary substring in the declared input for a true match.
// A null prefilter requests all available inputs. Missing inputs keep unknown semantics.
public sealed record SearchExtension(string Id, string Name, string Operator, string OperatorName, SearchInput Input,
    Func<string,string?> CandidateContains,
    Func<IReadOnlyList<SearchCandidate>,CancellationToken,Task<IReadOnlyList<SearchDerived>>> Extract,
    Func<string,IReadOnlyList<SearchCandidate>,CancellationToken,Task<IReadOnlyList<SearchDecision>>> Match)
{
    public Func<Guid,bool> CanRead { get; init; } = _ => true;
}
