namespace MyMailBox.Contracts;

public sealed record PstFolderPreview(string Path, uint Items);
public sealed record PstPreview(string File, string Fingerprint, int? AnsiCodePage, PstFolderPreview[] Folders);
public sealed record PstProgress(int Imported, int Duplicates, int Skipped, int Failed, string Current);
public sealed record PstResult(PstProgress Counts, string Report);
public interface IPstImport
{
    Task<IReadOnlyList<LocalMailboxInfo>> Targets(CancellationToken token);
    Task<PstPreview> Preview(string file, int? ansiCodePage, CancellationToken token);
    Task<PstResult> Import(PstPreview preview, Guid target, IProgress<PstProgress>? progress, CancellationToken token);
}
