namespace MyMailBox.Contracts;

[Flags]
public enum MailboxFeatures
{
    Read = 1, Send = 2, Delta = 4, Upload = 8, Move = 16, Mark = 32, Quota = 64, Search = 128
}

public sealed record MailboxType(string Id, string Name, MailboxFeatures Features);
public sealed record MailboxIdentity(Guid Id, string Address);
public sealed record MailboxFolder(string Id, string Name, string Role);
public sealed record MailboxItem(string Id, string Subject, string Sender, DateTimeOffset? Date, bool Read)
{
    /// <summary>In a delta page, every change to this item since the supplied cursor preserves its MIME bytes.</summary>
    public bool OriginalUnchanged { get; init; }
}
public sealed record MailboxPageResult(MailboxItem[] Items, string? Next);
public sealed record MailboxDelta(MailboxItem[] Changed, string[] Removed, string Cursor, bool More);
public sealed record MailboxQuota(long Used, long? Limit);

/// <summary>Folder and message IDs and cursors are opaque and scoped to one account. Caller owns all streams.</summary>
public interface IMailboxProvider
{
    MailboxType Type { get; }
    Task Connect(MailboxIdentity account, CancellationToken token);
    Task<MailboxFolder[]> Folders(MailboxIdentity account, CancellationToken token);
    Task<MailboxPageResult> List(MailboxIdentity account, string folder, string? cursor, int limit, CancellationToken token);
    Task Read(MailboxIdentity account, string folder, string item, Stream destination, CancellationToken token);
    Task<MailboxDelta> Delta(MailboxIdentity account, string folder, string? cursor, int limit, CancellationToken token) => throw new NotSupportedException();
    Task<SendOutcome> Send(MailboxIdentity account, Guid operation, Stream mime, string[] envelope, CancellationToken token) => throw new NotSupportedException();
    Task<string> Upload(MailboxIdentity account, string folder, Stream mime, CancellationToken token) => throw new NotSupportedException();
    Task<string> Move(MailboxIdentity account, string folder, string item, string destination, CancellationToken token) => throw new NotSupportedException();
    Task Mark(MailboxIdentity account, string folder, string item, bool read, CancellationToken token) => throw new NotSupportedException();
    Task<MailboxQuota> Quota(MailboxIdentity account, CancellationToken token) => throw new NotSupportedException();
    Task<MailboxPageResult> Search(MailboxIdentity account, string folder, string query, string? cursor, int limit, CancellationToken token) => throw new NotSupportedException();
}
