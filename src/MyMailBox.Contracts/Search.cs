namespace MyMailBox.Contracts;

public enum SearchKind { Text, Number, LocalDate, Boolean }
public enum SearchMatch { Contains, Equals, Before, OnOrAfter, AtLeast, AtMost, StartsWith, EndsWith, Regex, Exists }
public enum SearchOrder { Newest, Oldest, Largest, Subject }
public sealed record SearchField(string Id, string Name, SearchKind Kind, SearchMatch[] Matches);
public sealed record FieldCriterion(string Field, SearchMatch Match, string Value);
public sealed record RemoteSearchPage(Guid? Batch, bool Complete, string Detail);
public sealed record SearchRequest(FieldCriterion[] Criteria, SearchOrder Order = SearchOrder.Newest, int Offset = 0, int Limit = 25)
{
    public Guid? RemoteBatch { get; init; }
    public ConditionTree? Condition { get; init; }
}
public sealed record SearchIndexProgress(int Completed, int Total);
public sealed record SearchExcerpt(string Before, string Match, string After);
public sealed record SearchHit(string Id, Guid Account, Guid Folder, string Path, string Subject, string From,
    DateTimeOffset? Sent, long Size, Guid? LocalId, uint? ImapUid, uint? Validity)
{
    public SearchExcerpt? Excerpt { get; init; }
}
public sealed record SearchPage(IReadOnlyList<SearchHit> Items, bool HasMore, int FullMessages, int Summaries, int MissingMetadata)
{
    public int IndexedBodies { get; init; }
    public int Unknown { get; init; }
    public IReadOnlyList<string> UnavailableFields { get; init; } = [];
}

public interface IMailSearch
{
    IReadOnlyList<SearchField> Fields { get; }
    Task<RemoteSearchPage> SearchRemote(SavedSearch definition, IProgress<SearchIndexProgress>? progress = null, CancellationToken token = default);
    Task<SearchLibrary> Library(CancellationToken token = default);
    Task Save(SavedSearch search, CancellationToken token = default);
    Task<SearchPage> Search(SearchRequest request, CancellationToken token = default);
    Task<ConditionResult> EvaluateSaved(string messageId, Guid savedId, CancellationToken token = default);
    Task<ConditionResult> Evaluate(string messageId, ConditionTree condition, CancellationToken token = default);
    Task RebuildBodyIndex(IProgress<SearchIndexProgress>? progress = null, CancellationToken token = default);
    Task<int> Position(SearchHit hit, SearchOrder order, CancellationToken token = default);
    Task<string> Read(SearchHit hit, CancellationToken token = default);
}
