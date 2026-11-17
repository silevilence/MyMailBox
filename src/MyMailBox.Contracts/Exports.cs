namespace MyMailBox.Contracts;

public sealed record ExportItem(Guid MessageId, string Subject);
public sealed record ExportRequest(ExportItem[] Items, string Directory)
{
    public ExportOptions Options { get; init; } = new();
}
public sealed record ExportOptions
{
    public string[] MetadataFields { get; init; } = ["subject", "date"];
    public string[] AttachmentExtensions { get; init; } = [];
    public string AttachmentNameTemplate { get; init; } = "{subject}-{name}";
    public bool IncludeInline { get; init; }
    public bool SaveAttachments { get; init; }
}
public sealed record ExportItemResult(Guid MessageId, string Status, string? Path, string Detail)
{
    public string? Part { get; init; }
}
public sealed record ExportSource(IMailStore Store, ISelectionService Selections);
public interface IMailPdfPrinter
{
    Task Print(string documentUri, SafeMailDocument document, string path, CancellationToken cancellation = default);
}
public sealed record ExportReport(ExportItemResult[] Items);
public interface IMessageExporter
{
    string FormatId { get; }
    string Name { get; }
    Task<ExportReport> Export(ExportRequest request, IProgress<ExportItemResult>? progress = null, CancellationToken cancellation = default);
}
