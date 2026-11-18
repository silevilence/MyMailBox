namespace MyMailBox.Contracts;

public enum MailMark { Read, Unread, Star, Unstar, AddLabel, RemoveLabel }
public sealed record ManagedFolder(Guid Id, string Name, string Path, bool CanRename, string Role);
public sealed record ManagedMessage(string Key, string Subject, bool Read, bool Starred, string[] Labels);
public sealed record ManagedPage(IReadOnlyList<ManagedMessage> Items, bool HasMore);
public sealed record MarkResult(string Key, bool Success, string Detail, ManagedMessage? Updated = null);
public sealed record OrganizationPage(IAccountService Accounts, IMailOrganizer Organizer)
{
    public IMailTransferService? Transfers { get; init; }
    public IMailActionService? Actions { get; init; }
}

public interface IMailOrganizer
{
    Task<IReadOnlyList<ManagedFolder>> Folders(Guid account, CancellationToken token = default);
    Task<Guid> Create(Guid account, Guid? parent, string name, CancellationToken token = default);
    Task Rename(Guid account, Guid folder, string name, CancellationToken token = default);
    Task DeleteEmpty(Guid account, Guid folder, CancellationToken token = default);
    Task<ManagedPage> Messages(Guid account, Guid folder, int offset, int limit, CancellationToken token = default);
    Task<IReadOnlyList<MarkResult>> Mark(Guid account, Guid folder, string[] keys, MailMark mark, string label, CancellationToken token = default);
}
