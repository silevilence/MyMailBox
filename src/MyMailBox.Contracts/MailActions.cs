namespace MyMailBox.Contracts;

public enum MailAction { Copy, Move, Archive, Trash, DeletePermanently }
public sealed record MailActionRequest(Guid Account, Guid Source, string[] Keys, MailAction Action, Guid? Destination = null);
public sealed record MailActionResult(string Key, bool Success, string Detail, string? DestinationKey = null, Guid? UndoId = null);

public interface IMailActionService
{
    Task<IReadOnlyList<MailActionResult>> Apply(MailActionRequest request, CancellationToken token = default);
    Task Undo(Guid operation, CancellationToken token = default);
}
