namespace MyMailBox.Contracts;

public enum MailTls { Implicit, StartTls }
public enum MailAuthentication { Password, OAuth2 }
public enum MailProxy { None, Http, Socks5 }
public sealed record MailEndpoint(string Host, int Port, string User, MailTls Tls = MailTls.Implicit,
    MailAuthentication Authentication = MailAuthentication.Password, MailProxy Proxy = MailProxy.None,
    string ProxyHost = "", int ProxyPort = 1080, string CertificateSha256 = "");
public sealed record ProtocolSettings(MailEndpoint? Incoming = null, MailEndpoint? Outgoing = null)
{
    public string? PresetId { get; init; }
}

public sealed record ProtocolPreset(string Id, string Name, string IncomingHost, string OutgoingHost,
    string Guidance, bool IdentifyClient = false)
{
    public int IncomingPort { get; init; } = 993;
    public int OutgoingPort { get; init; } = 465;
    public ProtocolSettings ForAddress(string address) => new(new(IncomingHost,IncomingPort,address),new(OutgoingHost,OutgoingPort,address)) { PresetId = Id };
}
public sealed record RemoteFolder(string Path, string Name, string Role, bool Selectable);
public sealed record RemoteSummary(uint Uid, string Subject, string From, DateTimeOffset? Date, long Size, bool IsRead);
public sealed record RemoteMailPage(IReadOnlyList<RemoteSummary> Items, int Offset, bool HasMore, uint UidValidity);
public sealed record ProtocolPage(IAccountService Accounts, IProtocolMailService Service)
{
    public IOfflineMarks? OfflineMarks { get; init; }
    public IOutboxService? Outbox { get; init; }
    public IImapSyncService? ImapSync { get; init; }
}
public sealed record TextMail(string To, string Cc, string Bcc, string Subject, string Body)
{
    public string? Html { get; init; }
    public string? FromIdentity {get;init;}
    public string? InReplyTo {get;init;}
    public string[] References {get;init;} = [];
    public Guid[] PartIds {get;init;} = [];
}
public sealed record SendOutcome(string State, string Message, IReadOnlyList<string> Accepted, IReadOnlyList<string> Rejected);
public sealed record PopOutcome(int Imported, int Existing, int DeleteRequested, bool HasMore, string Message);
public interface IProtocolMailService
{
    Task<IReadOnlyList<ProtocolPreset>> Presets(CancellationToken cancellation = default) => Task.FromResult<IReadOnlyList<ProtocolPreset>>([]);
    Task<ProtocolSettings> Settings(Guid account, CancellationToken cancellation = default);
    Task Save(Guid account, ProtocolSettings settings, string? incomingSecret, string? outgoingSecret, CancellationToken cancellation = default);
    Task<IReadOnlyList<RemoteFolder>> Folders(Guid account, CancellationToken cancellation = default);
    Task<RemoteMailPage> Summaries(Guid account, string folder, int offset, int limit, CancellationToken cancellation = default);
    Task<SendOutcome> Send(Guid account, TextMail message, CancellationToken cancellation = default);
    Task<PopOutcome> ReceivePop(Guid account, bool deleteAfterDownload, int limit = 100, CancellationToken cancellation = default);
}
