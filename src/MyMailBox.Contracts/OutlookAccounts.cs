namespace MyMailBox.Contracts;

public sealed record OutlookEndpoint(string Host, int? Port, string User);
public sealed record OutlookAccountPreview(string Source, string Name, string Address, string Provider, OutlookEndpoint? Incoming, OutlookEndpoint? Outgoing)
{
    public Guid? ImportedAccount { get; init; }
    public bool SourceChanged { get; init; }
}
public interface IOutlookAccountImport
{
    Task<IReadOnlyList<string>> Profiles(CancellationToken token);
    Task<IReadOnlyList<OutlookAccountPreview>> Preview(string profile, CancellationToken token);
    Task<IReadOnlyList<MailAccount>> Matches(string provider, string address, CancellationToken token);
    Task<MailAccount> ImportSource(string profile, OutlookAccountPreview source, AccountEdit edit, ProtocolSettings settings, Guid? mapTo, CancellationToken token) =>
        Import(edit, settings, mapTo, token);
    Task<MailAccount> Import(AccountEdit edit, ProtocolSettings settings, Guid? mapTo, CancellationToken token);
}
