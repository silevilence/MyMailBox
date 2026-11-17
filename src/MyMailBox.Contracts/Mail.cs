namespace MyMailBox.Contracts;

public sealed record MailFolder(Guid Id, Guid AccountId, string Name, string Path, string Role = "custom", Guid? ParentId = null);
public sealed record MailSummary(Guid Id, Guid FolderId, string Subject, string From, string To, DateTimeOffset Date, long Size, bool IsRead = false);
public sealed record MailPageResult(IReadOnlyList<MailSummary> Items, int Offset, bool HasMore);
public sealed record MimePartInfo(string Path, string ContentType, string Name, string? ContentId, bool Attachment, int Depth);
public sealed record ParsedMail(string Subject, string From, string To, DateTimeOffset Date, string Text,
    string Cc = "", string Html = "", IReadOnlyList<MimePartInfo>? Parts = null, IReadOnlyList<string>? Warnings = null, IReadOnlyList<MailResource>? InlineResources = null);

/// <summary>Raw MIME remains authoritative. Summaries never load message bodies.</summary>
public interface IMailStore
{
    Task<IReadOnlyList<MailFolder>> Folders(CancellationToken cancellation = default);
    Task<MailPageResult> List(Guid folderId, int offset, int limit, CancellationToken cancellation = default);
    Task<Guid> Import(Guid folderId, byte[] raw, ParsedMail metadata, CancellationToken cancellation = default);
    Task<byte[]> Read(Guid messageId, CancellationToken cancellation = default);
}

public interface IMimeReader
{
    Task<ParsedMail> Parse(byte[] raw, CancellationToken cancellation = default);
}

public sealed record MailReader(IMailStore Store, IMimeReader Parser, IHtmlMailRenderer? Html = null, IRemoteContentService? Remote = null, IAttachmentService? Attachments = null);
