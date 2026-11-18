namespace MyMailBox.Contracts;

public sealed record OutboxItem(Guid Id, Guid Account, string Subject, string State, string Detail,
    string[] Accepted, string[] Remaining, DateTimeOffset Created, bool CopyPending);

public interface IOutboxService
{
    Task Enqueue(Guid id, Guid account, TextMail message, CancellationToken cancellation = default);
    Task<SendOutcome> Deliver(Guid id, CancellationToken cancellation = default);
    Task<IReadOnlyList<OutboxItem>> List(Guid account, int offset, int limit, CancellationToken cancellation = default);
    Task<IReadOnlyList<string>> History(Guid id, CancellationToken cancellation = default);
    Task Cancel(Guid id, CancellationToken cancellation = default);
    Task Retry(Guid id, CancellationToken cancellation = default);
    Task SaveCopy(Guid id, CancellationToken cancellation = default);
}
