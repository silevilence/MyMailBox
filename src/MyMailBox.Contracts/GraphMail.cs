namespace MyMailBox.Contracts;

public sealed record GraphSettings(string ClientId = "", bool SignedIn = false);
public sealed record GraphFolder(string Id, string Name, int ChildCount);
public sealed record GraphSummary(string Id, string Subject, string From, DateTimeOffset? Date, bool IsRead, bool HasAttachments);
public sealed record GraphFolders(IReadOnlyList<GraphFolder> Items, string? Next);
public sealed record GraphMessages(IReadOnlyList<GraphSummary> Items, string? Next);
public sealed record GraphPage(IAccountService Accounts, IGraphMailService Service);
public interface IGraphMailService
{
    Task<GraphSettings> Settings(Guid account, CancellationToken cancellation = default);
    Task SaveClientId(Guid account, string clientId, CancellationToken cancellation = default);
    Task SignIn(Guid account, CancellationToken cancellation = default);
    Task Disconnect(Guid account, CancellationToken cancellation = default);
    Task<GraphFolders> Folders(Guid account, string? parent = null, string? next = null, CancellationToken cancellation = default);
    Task<GraphMessages> Messages(Guid account, string folder, string? next = null, CancellationToken cancellation = default);
    Task<Guid> Download(Guid account, string message, CancellationToken cancellation = default);
}
