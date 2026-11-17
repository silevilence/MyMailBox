namespace MyMailBox.Contracts;

public sealed record BackupFile(string Path, long Size, string Sha256);
public sealed record WorkspaceBackupManifest(int Schema, DateTimeOffset Created, BackupFile[] Files, bool CredentialsIncluded = false);
public interface IWorkspaceBackup
{
    Task<string> Create(string destination, CancellationToken cancellation = default);
    Task<string> Restore(string source, string newWorkspace, CancellationToken cancellation = default);
}
