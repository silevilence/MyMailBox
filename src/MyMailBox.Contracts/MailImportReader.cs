namespace MyMailBox.Contracts;

public sealed record ImportParameter(string Id, string Label, string Default = "");
public sealed record ImportFormat(string Id, string Name, string Pattern, int Version, IReadOnlyList<ImportParameter> Parameters);
public sealed record ImportReadRequest(string File, IReadOnlyDictionary<string, string> Parameters, bool Preview, Guid? Target, IReadOnlyCollection<uint> Committed);
// Node identifiers are stable within a file. Emit each parent folder before its children.
// A reader may omit committed content and emit a duplicate row carrying only its node and parent.
public sealed record MailImportItem(string Kind, uint Node, uint? Parent, string? Name, uint Count = 0,
    byte[]? Raw = null, bool IsRead = false, DateTime? Received = null, DateTime? Submitted = null, string? Detail = null, string[]? Categories = null);
public delegate Task VisitImportSource(ImportReadRequest request, Func<MailImportItem, CancellationToken, Task> receive, CancellationToken token);
public sealed record MailImportReader(ImportFormat Format, VisitImportSource Read);
