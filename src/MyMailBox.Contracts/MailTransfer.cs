namespace MyMailBox.Contracts;

public sealed record TransferRequest(Guid Id, Guid SourceAccount, Guid SourceFolder, string Key, Guid Destination, bool Move);
public sealed record MailTransfer(TransferRequest Request, string SourcePath, string DestinationPath, string Phase,
    string Detail, Guid? TargetMessage, string? Sha256, DateTimeOffset Created);

public sealed record TransferEvent(DateTimeOffset Time, string Phase, string Detail);

public interface IMailTransferService
{
    Task<IReadOnlyList<MailFolder>> Destinations(Guid sourceAccount, CancellationToken token = default);
    Task<MailTransfer> Plan(TransferRequest request, CancellationToken token = default);
    Task Cancel(Guid operation, CancellationToken token = default);
    Task<IReadOnlyList<TransferEvent>> History(Guid operation, CancellationToken token = default);
    Task<MailTransfer> Continue(Guid operation, IProgress<MailTransfer>? progress = null, CancellationToken token = default);
    Task<IReadOnlyList<MailTransfer>> Recent(Guid sourceAccount, int offset, int limit, CancellationToken token = default);
}
