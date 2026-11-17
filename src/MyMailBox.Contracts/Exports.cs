namespace MyMailBox.Contracts;

public sealed record ExportItem(Guid MessageId, string Subject);
public sealed record ExportRequest(ExportItem[] Items, string Directory);
public sealed record ExportItemResult(Guid MessageId, string Status, string? Path, string Detail);
public sealed record ExportReport(ExportItemResult[] Items);
public interface IMessageExporter
{
    string FormatId { get; }
    string Name { get; }
    Task<ExportReport> Export(ExportRequest request, IProgress<ExportItemResult>? progress = null, CancellationToken cancellation = default);
}
