namespace MyMailBox.Contracts;

public sealed record ImapCachedMail(Guid FolderId, uint Validity, uint Uid, string Subject, string Sender, long Size, Guid? LocalId, bool Missing)
{
    public bool IsRead { get; init; }
    public bool IsStarred { get; init; }
}
public sealed record ImapCachePage(IReadOnlyList<ImapCachedMail> Items, bool HasMore);
public sealed record ImapSyncResult(int Added, uint Validity, uint? NextUid, int Count);
public sealed record ImapReading(string Subject, string Text, Guid LocalId);

public interface IImapSyncService
{
    Task<IReadOnlyList<string>> CachedFolders(Guid account, CancellationToken cancellation = default);
    Task<ImapCachePage> CachedPage(Guid account, string path, int offset, int limit, CancellationToken cancellation = default);
    Task<ImapSyncResult> Sync(Guid account, string path, int recentCount, IProgress<int>? progress = null, CancellationToken cancellation = default);
    Task WaitForChange(Guid account, string path, ImapSyncResult previous, CancellationToken cancellation = default);
    Task<ImapReading> OpenMessage(Guid account, string path, ImapCachedMail message, bool allowDownload, CancellationToken cancellation = default);
}


public sealed record OfflineMark(Guid Id, string Path, string Subject, uint Uid, MailMark Mark, string State, string Detail);
public interface IOfflineMarks
{
    Task Queue(Guid account, string path, ImapCachedMail message, MailMark mark, CancellationToken token = default);
    Task<IReadOnlyList<OfflineMark>> Changes(Guid account, CancellationToken token = default);
    Task Dismiss(Guid account, Guid edit, CancellationToken token = default);
}
