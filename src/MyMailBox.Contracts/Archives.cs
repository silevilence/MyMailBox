namespace MyMailBox.Contracts;

public sealed record ArchiveMessage(MailSummary Summary, string Entry, string Sha256);
public sealed record MailArchiveManifest(int Schema, MailAccount[] Accounts, MailFolder[] Folders, ArchiveMessage[] Messages);
public sealed record ArchiveImportResult(int Accounts, int Folders, int Messages, IReadOnlyDictionary<Guid, Guid> AccountMapping,
    IReadOnlyDictionary<Guid, Guid> FolderMapping, IReadOnlyDictionary<Guid, Guid> MessageMapping, string[] Notes);
public interface IMailArchive
{
    Task<string> Export(Guid[] folders, string destination, CancellationToken cancellation = default);
    Task<ArchiveImportResult> Import(string archive, CancellationToken cancellation = default);
}
