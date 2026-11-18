namespace MyMailBox.Contracts;

public enum SearchKind { Text, Number, LocalDate, Boolean }
public enum SearchMatch { Contains, Equals, Before, OnOrAfter, AtLeast, AtMost }
public enum SearchOrder { Newest, Oldest, Largest, Subject }
public sealed record SearchField(string Id, string Name, SearchKind Kind, SearchMatch[] Matches);
public sealed record FieldCriterion(string Field, SearchMatch Match, string Value);
public sealed record SearchRequest(FieldCriterion[] Criteria, SearchOrder Order = SearchOrder.Newest, int Offset = 0, int Limit = 25);
public sealed record SearchHit(string Id, Guid Account, Guid Folder, string Path, string Subject, string From,
    DateTimeOffset? Sent, long Size, Guid? LocalId, uint? ImapUid, uint? Validity);
public sealed record SearchPage(IReadOnlyList<SearchHit> Items, bool HasMore, int FullMessages, int Summaries, int MissingMetadata);

public interface IMailSearch
{
    IReadOnlyList<SearchField> Fields { get; }
    Task<SearchPage> Search(SearchRequest request, CancellationToken token = default);
    Task<int> Position(SearchHit hit, SearchOrder order, CancellationToken token = default);
    Task<string> Read(SearchHit hit, CancellationToken token = default);
}
