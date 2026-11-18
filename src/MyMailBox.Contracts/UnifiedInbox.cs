namespace MyMailBox.Contracts;

public sealed record InboxPosition(long UnixMilliseconds, string Key);
public sealed record InboxMessage(Guid Account, string AccountName, InboxPosition Position, string Subject, string Sender, bool Cached);
public sealed record UnifiedPage(IReadOnlyList<InboxMessage> Messages, bool HasMore);
public sealed record ReceiveStatus(Guid Account, string Name, bool Available, bool Automatic, int Range, string State, string Detail, DateTimeOffset? LastSuccess);

/// <summary>Each provider exposes its local inbox in descending (time, globally unique ordinal key) order.</summary>
public interface IReceiveProvider
{
    bool Supports(MailAccount account);
    Task Receive(MailAccount account, int range, CancellationToken token);
    Task<IReadOnlyList<InboxMessage>> Inbox(MailAccount account, InboxPosition? before, int limit, CancellationToken token);
    Task<string> Open(MailAccount account, string key, CancellationToken token);
}

public interface IUnifiedInbox
{
    Task<IReadOnlyList<ReceiveStatus>> Status(CancellationToken token = default);
    Task Configure(Guid account, bool automatic, int range, CancellationToken token = default);
    Task Refresh(Guid account, CancellationToken token = default);
    Task<UnifiedPage> Inbox(Guid? account, InboxPosition? before, int limit, CancellationToken token = default);
    Task<string> Open(Guid account, string key, CancellationToken token = default);
}
