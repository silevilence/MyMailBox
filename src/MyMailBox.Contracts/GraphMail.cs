namespace MyMailBox.Contracts;

public sealed record GraphSettings(bool SignedIn = false) { public bool CanWrite { get; init; } }
public sealed record GraphFolder(string Id, string Name, int ChildCount);
public sealed record GraphSummary(string Id, string Subject, string From, DateTimeOffset? Date, bool IsRead, bool HasAttachments);
public sealed record GraphFolders(IReadOnlyList<GraphFolder> Items, string? Next);
public sealed record GraphMessages(IReadOnlyList<GraphSummary> Items, string? Next);
public sealed record GraphPage(IAccountService Accounts, IGraphMailService Service)
{
    public IGraphFolderSync? Sync { get; init; }
    public IGraphWrites? Writes { get; init; }
}
public interface IGraphWrites
{
    Task Enable(Guid account, CancellationToken token);
    Task MarkRead(Guid account, string message, bool read, CancellationToken token);
    Task<string> Move(Guid account, string message, string destination, CancellationToken token);
    Task<string> SaveDraft(MailDraft draft, CancellationToken token);
}
public sealed record GraphSyncScope(string Id, string Name, DateTimeOffset? LastSuccess, int Messages);
public sealed record GraphOfflinePage(IReadOnlyList<GraphSummary> Items, bool More);
public interface IGraphFolderSync
{
    Task<GraphSyncScope> Sync(Guid account, GraphFolder folder, CancellationToken token);
    Task<IReadOnlyList<GraphSyncScope>> Scopes(Guid account, CancellationToken token);
    Task<GraphOfflinePage> Cached(Guid account, string folder, int offset, CancellationToken token);
}
public interface IGraphMailService
{
    Task<GraphSettings> Settings(Guid account, CancellationToken cancellation = default);
    Task SignIn(Guid account, CancellationToken cancellation = default);
    Task Disconnect(Guid account, CancellationToken cancellation = default);
    Task<GraphFolders> Folders(Guid account, string? parent = null, string? next = null, CancellationToken cancellation = default);
    Task<GraphMessages> Messages(Guid account, string folder, string? next = null, CancellationToken cancellation = default);
    Task<Guid> Download(Guid account, string message, CancellationToken cancellation = default);
}
