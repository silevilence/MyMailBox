namespace MyMailBox.Contracts;

public enum SelectionKind { Account, Folder, Identity }
public sealed record SelectionReference(SelectionKind Kind, Guid AccountId, Guid? FolderId = null);
public sealed record SelectionChoice(SelectionReference Reference, string Name, string Address, string Provider, string Color,
    string[] Tags, string Path, string Role, Guid? ParentId, bool Available, string Reason, bool Recent);
public sealed record SelectionQuery(SelectionKind Kind, AccountCapabilities Required = AccountCapabilities.None, string Search = "", string? Tag = null, bool RecentOnly = false);
public sealed record SavedSelection(bool DynamicTags, string[] Tags, SelectionReference[] References, SelectionKind Kind, AccountCapabilities Required);
public sealed record ResolvedSelection(SelectionChoice[] Choices, SelectionReference[] Missing);
public interface ISelectionService
{
    Task<IReadOnlyList<SelectionChoice>> Query(SelectionQuery query, CancellationToken cancellation = default);
    Task Save(string slot, SavedSelection selection, CancellationToken cancellation = default);
    Task<SavedSelection?> Load(string slot, CancellationToken cancellation = default);
    Task<ResolvedSelection> Resolve(SavedSelection selection, CancellationToken cancellation = default);
}
