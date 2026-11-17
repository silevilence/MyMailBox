namespace MyMailBox.Contracts;

public sealed record LocalMailboxInfo(Guid Id, string Name, string? Alias, string[] Tags, bool Open, bool Removed, int Folders, int Messages);
public interface ILocalMailboxService
{
    string StorageDescription { get; }
    Task<IReadOnlyList<LocalMailboxInfo>> List(CancellationToken cancellation = default);
    Task<MailAccount> Create(AccountEdit edit, CancellationToken cancellation = default);
    Task SetOpen(Guid id, bool open, string? alias, CancellationToken cancellation = default);
    Task<IReadOnlyList<MailFolder>> Folders(Guid id, CancellationToken cancellation = default);
    Task<MailFolder> CreateFolder(Guid account, Guid? parent, string name, CancellationToken cancellation = default);
    Task<Guid> Archive(Guid folder, byte[] raw, CancellationToken cancellation = default);
}
