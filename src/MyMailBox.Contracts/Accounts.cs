namespace MyMailBox.Contracts;

[Flags]
public enum AccountCapabilities { None = 0, Receive = 1, Send = 2, Folders = 4, Import = 8 }
public sealed record MailAccount(Guid Id, string Name, string Provider, string Address, string? Alias,
    string[] Tags, string Color, string Notes, int Order, bool Enabled, AccountCapabilities Capabilities,
    string? CredentialReference, string AuthenticationState);
public sealed record AccountEdit(Guid? Id, string Name, string Provider, string Address = "", string? Alias = null,
    string[]? Tags = null, string Color = "#2563EB", string Notes = "", int Order = 0, bool Enabled = true,
    AccountCapabilities Capabilities = AccountCapabilities.Receive | AccountCapabilities.Send);

public interface IAccountService
{
    IReadOnlyList<MailboxType> ProviderTypes => [];
    Task<IReadOnlyList<MailAccount>> List(CancellationToken cancellation = default);
    Task<MailAccount> Save(AccountEdit edit, string? newCredential = null, CancellationToken cancellation = default);
    Task Remove(Guid id, CancellationToken cancellation = default);
}
