namespace MyMailBox.Contracts;

public sealed record AttachmentPreview(string Kind, string Text, MailResource? Resource = null);
public sealed record SavedAttachment(string Path, long Bytes, bool CanOpenExternally, string Notice);
public interface IAttachmentPreviewer
{
    bool Supports(string contentType);
    Task<AttachmentPreview> Preview(string contentType, byte[] data, CancellationToken cancellation = default);
}
public interface IAttachmentService
{
    Task<SavedAttachment> Save(Guid message, string part, string directory, IProgress<long>? progress = null, CancellationToken cancellation = default);
    Task<AttachmentPreview> Preview(Guid message, string part, CancellationToken cancellation = default);
}
