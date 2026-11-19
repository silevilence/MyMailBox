namespace MyMailBox.Contracts;

public sealed record OutlookEndpoint(string Host, int? Port, string User);
public sealed record OutlookAccountPreview(string Source, string Name, string Address, string Provider, OutlookEndpoint? Incoming, OutlookEndpoint? Outgoing);
public interface IOutlookAccountImport
{
    Task<IReadOnlyList<string>> Profiles(CancellationToken token);
    Task<IReadOnlyList<OutlookAccountPreview>> Preview(string profile, CancellationToken token);
    Task<IReadOnlyList<MailAccount>> Matches(string provider, string address, CancellationToken token);
    Task<MailAccount> Import(AccountEdit edit, ProtocolSettings settings, Guid? mapTo, CancellationToken token);
}
