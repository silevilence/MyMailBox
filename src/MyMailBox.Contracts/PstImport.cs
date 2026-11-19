namespace MyMailBox.Contracts;

public sealed record PstFolderPreview(string Path, uint Items);
public sealed record PstPreview(string File, string Fingerprint, int? AnsiCodePage, PstFolderPreview[] Folders)
{
    public string Format { get; init; } = "pst";
    public int Version { get; init; } = 1;
    public Dictionary<string,string> Parameters { get; init; } = [];
}
public sealed record PstProgress(int Imported, int Duplicates, int Skipped, int Failed, string Current);
public sealed record PstResult(PstProgress Counts, string Report);
public sealed record PstSession(string File, string Fingerprint, int? CodePage, Guid Target, string TargetName, int Committed, string Report,
    string Format, int Version, Dictionary<string,string> Parameters);
public interface IPstImport
{
    Task<IReadOnlyList<LocalMailboxInfo>> Targets(CancellationToken token);
    Task<IReadOnlyList<PstSession>> Pending(CancellationToken token);
    IReadOnlyList<ImportFormat> Formats { get; }
    Task<PstPreview> PreviewFile(string format, string file, Dictionary<string,string> parameters, CancellationToken token);
    Task<PstPreview> Preview(string file, int? ansiCodePage, CancellationToken token);
    Task<PstResult> Import(PstPreview preview, Guid target, IProgress<PstProgress>? progress, CancellationToken token);
}
