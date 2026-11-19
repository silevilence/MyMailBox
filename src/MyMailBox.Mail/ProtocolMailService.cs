using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

/// <summary>Short-lived protocol sessions: no timers, implicit sends, or automatic retries.</summary>
public sealed partial class ProtocolMailService : IProtocolMailService, IImapSyncService, IOutboxService, IMailOrganizer, IMailActionService, IMailTransferService, IOfflineMarks
{
    public GraphMailService? Graph { private get; init; }
    private async Task<MailAccount> SendingAccount(Guid account, CancellationToken token)
    {
        var sender = (await _accounts.List(token)).SingleOrDefault(a => a.Id == account && a.Enabled && a.Capabilities.HasFlag(AccountCapabilities.Send));
        if (sender == null || !(sender.Provider is "imap-smtp" or "smtp" || sender.Provider == "outlook-com" && Graph != null))
            throw new InvalidDataException("账户已停用、移除，或不支持此操作。");
        return sender;
    }
    private readonly SqliteMailStore _store;
    private readonly MailDraftStore _draftStore;
    private readonly LocalMailActions _localActions;
    private readonly TransferJournal _transfers;
    private readonly AccountService _accounts;
    private readonly Func<ImapClient> _imapClients;
    private readonly Func<IReadOnlyList<ProtocolPreset>> _presets;
    public Task<IReadOnlyList<ProtocolPreset>> Presets(CancellationToken cancellation = default) => Task.FromResult(_presets());
    public ProtocolMailService(SqliteMailStore store, Func<IReadOnlyList<ProtocolPreset>> presets)
        : this(store, () => new ImapClient(), presets) { }
    public ProtocolMailService(SqliteMailStore store) : this(store, () => new ImapClient()) { }
    public ProtocolMailService(SqliteMailStore store, Func<ImapClient> imapClients, Func<IReadOnlyList<ProtocolPreset>>? presets = null)
    {
        _draftStore=new(store);
        _imapClients = imapClients;
        _presets = presets ?? (() => []);
        _store = store; _accounts = new(store);
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS automatic_mail(
                operation TEXT PRIMARY KEY, sender TEXT NOT NULL, fingerprint TEXT NOT NULL, recipient TEXT COLLATE NOCASE NOT NULL,
                kind TEXT NOT NULL, at INTEGER NOT NULL, UNIQUE(sender,fingerprint,recipient,kind));
            CREATE INDEX IF NOT EXISTS automatic_mail_rate ON automatic_mail(sender,at);
            CREATE TABLE IF NOT EXISTS protocol_settings(account_id TEXT PRIMARY KEY REFERENCES accounts(id), settings TEXT NOT NULL, incoming_ref TEXT, outgoing_ref TEXT);
            CREATE TRIGGER IF NOT EXISTS remove_protocol_credentials AFTER UPDATE OF removed ON accounts WHEN NEW.removed=1 BEGIN
                DELETE FROM credentials WHERE id IN (SELECT incoming_ref FROM protocol_settings WHERE account_id=NEW.id UNION SELECT outgoing_ref FROM protocol_settings WHERE account_id=NEW.id);
                DELETE FROM protocol_settings WHERE account_id=NEW.id;
            END;
            """;
        command.ExecuteNonQuery();
        ImapCache.Initialize(store);
        OutboxStore.Initialize(store);
        _outbox = new(store);
        OrganizationStore.Initialize(store);
        _organization = new(store);
        LocalMailActions.Initialize(store);
        _localActions = new(store);
        TransferJournal.Initialize(store);
        _transfers = new(store);
    }
    private async Task<MailAccount> Account(Guid id, AccountCapabilities required, CancellationToken token)
    {
        var account = (await _accounts.List(token)).SingleOrDefault(a => a.Id == id && a.Enabled);
        if (account is null || (account.Capabilities & required) != required || account.Provider is not ("imap" or "imap-smtp" or "smtp" or "pop3"))
            throw new InvalidDataException("账户已停用、移除，或不支持此操作。");
        return account;
    }
    public async Task<ProtocolSettings> Settings(Guid account, CancellationToken cancellation = default)
    {
        await Account(account, AccountCapabilities.None, cancellation);
        using var db = _store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT settings FROM protocol_settings WHERE account_id=$id"; command.Parameters.AddWithValue("$id", account.ToString());
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ProtocolSettings>(json)! : new();
    }
    public async Task Save(Guid account, ProtocolSettings settings, string? incomingSecret, string? outgoingSecret, CancellationToken cancellation = default)
    {
        var identity = await Account(account, AccountCapabilities.None, cancellation);
        if (identity.Capabilities.HasFlag(AccountCapabilities.Receive)) Validate(settings.Incoming ?? throw new InvalidDataException("请配置收信服务器。"));
        if (identity.Capabilities.HasFlag(AccountCapabilities.Send)) Validate(settings.Outgoing ?? throw new InvalidDataException("请配置发信服务器。"));
        if (settings.PresetId is not null && identity.Provider != "imap-smtp") throw new InvalidDataException("此预设需要 IMAP 与 SMTP 收发账户。");
        ResolvePreset(settings);
        using var db = _store.Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT incoming_ref,outgoing_ref,settings FROM protocol_settings WHERE account_id=$id"; command.Parameters.AddWithValue("$id", account.ToString());
        string? incoming = null, outgoing = null; ProtocolSettings? previous = null;
        using (var reader = command.ExecuteReader()) if (reader.Read()) { incoming = reader.IsDBNull(0) ? null : reader.GetString(0); outgoing = reader.IsDBNull(1) ? null : reader.GetString(1); previous = JsonSerializer.Deserialize<ProtocolSettings>(reader.GetString(2)); }
        string? Protect(string? secret, string? reference)
        {
            if (string.IsNullOrEmpty(secret)) return reference;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var bytes = Encoding.UTF8.GetBytes(secret);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            reference ??= Guid.NewGuid().ToString();
            using var save = db.CreateCommand(); save.Transaction = tx; save.CommandText = "INSERT INTO credentials VALUES($id,$secret) ON CONFLICT(id) DO UPDATE SET protected=excluded.protected";
            save.Parameters.AddWithValue("$id", reference); save.Parameters.AddWithValue("$secret", encrypted); save.ExecuteNonQuery(); return reference;
        }
        static bool Changed(MailEndpoint? prior, MailEndpoint? next) => next is not null &&
            (prior is null || !prior.Host.Equals(next.Host,StringComparison.OrdinalIgnoreCase) || prior.User != next.User || prior.Authentication != next.Authentication);
        if ((incoming != null && Changed(previous?.Incoming,settings.Incoming) && string.IsNullOrEmpty(incomingSecret)) ||
            (outgoing != null && Changed(previous?.Outgoing,settings.Outgoing) && string.IsNullOrEmpty(outgoingSecret)))
            throw new InvalidDataException("服务器、认证方式或登录身份已更改，请重新输入对应凭据后保存。");
        string? SaveSecret(MailEndpoint? endpoint,string? value,string? reference)
        {
            if (endpoint is not null) return Protect(value,reference);
            if (reference is not null)
            {
                using var forget = db.CreateCommand();forget.Transaction=tx;
                forget.CommandText="DELETE FROM credentials WHERE id=$ref";forget.Parameters.AddWithValue("$ref",reference);forget.ExecuteNonQuery();
            }
            return null;
        }
        incoming = SaveSecret(settings.Incoming,incomingSecret,incoming);
        outgoing = SaveSecret(settings.Outgoing,outgoingSecret,outgoing);
        command.CommandText = "INSERT INTO protocol_settings VALUES($id,$settings,$incoming,$outgoing) ON CONFLICT(account_id) DO UPDATE SET settings=excluded.settings,incoming_ref=excluded.incoming_ref,outgoing_ref=excluded.outgoing_ref";
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings)); command.Parameters.AddWithValue("$incoming", (object?)incoming ?? DBNull.Value); command.Parameters.AddWithValue("$outgoing", (object?)outgoing ?? DBNull.Value);
        cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); tx.Commit();
    }
    public static void Validate(MailEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host) || Uri.CheckHostName(endpoint.Host) == UriHostNameType.Unknown || endpoint.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(endpoint.User))
            throw new InvalidDataException("请填写服务器主机名、1–65535 端口和登录用户名。");
        if (!Enum.IsDefined(endpoint.Tls) || !Enum.IsDefined(endpoint.Authentication) || !Enum.IsDefined(endpoint.Proxy)) throw new InvalidDataException("连接方式无效。");
        if (endpoint.Proxy != MailProxy.None && (Uri.CheckHostName(endpoint.ProxyHost) == UriHostNameType.Unknown || endpoint.ProxyPort is < 1 or > 65535)) throw new InvalidDataException("代理主机或端口无效。");
        if (endpoint.CertificateSha256.Length > 0 && (endpoint.CertificateSha256.Length != 64 || !endpoint.CertificateSha256.All(Uri.IsHexDigit))) throw new InvalidDataException("证书指纹须为 64 位 SHA-256 十六进制值。");
    }
    private ProtocolPreset? ResolvePreset(ProtocolSettings settings)
    {
        if (settings.PresetId is null) return null;
        var preset = _presets().FirstOrDefault(p => p.Id == settings.PresetId)
            ?? throw new InvalidDataException("账户的预设插件已停用，请重新启用，或明确选择自定义服务器后保存。");
        static bool Matches(MailEndpoint? endpoint,string host,int port) => endpoint is null ||
            (endpoint.Host.Equals(host,StringComparison.OrdinalIgnoreCase) && endpoint.Port==port && endpoint.Tls==MailTls.Implicit &&
             endpoint.Authentication==MailAuthentication.Password);
        if (!Matches(settings.Incoming,preset.IncomingHost,preset.IncomingPort) || !Matches(settings.Outgoing,preset.OutgoingHost,preset.OutgoingPort))
            throw new InvalidDataException("已保存服务器与当前预设不一致，请重新应用预设或选择自定义服务器。");
        return preset;
    }
    private async Task<MailEndpoint> Connect(MailService client, Guid id, bool outgoing, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var settings = await Settings(id, token);
        var preset = ResolvePreset(settings);
        var endpoint = (outgoing ? settings.Outgoing : settings.Incoming) ?? throw new InvalidDataException("请先保存对应的连接配置。");
        Validate(endpoint);
        using var db = _store.Open(); using var command = db.CreateCommand();
        command.CommandText = outgoing ? "SELECT protected FROM credentials JOIN protocol_settings ON credentials.id=outgoing_ref WHERE account_id=$id" : "SELECT protected FROM credentials JOIN protocol_settings ON credentials.id=incoming_ref WHERE account_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        if (command.ExecuteScalar() is not byte[] encrypted) throw new InvalidDataException("请在连接配置中填写密码／授权码或访问令牌；恢复备份后需要重新授权。");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            client.Timeout = 15000;
            client.ProxyClient = endpoint.Proxy switch { MailProxy.Http => new HttpProxyClient(endpoint.ProxyHost, endpoint.ProxyPort), MailProxy.Socks5 => new Socks5Client(endpoint.ProxyHost, endpoint.ProxyPort), _ => null };
            if (endpoint.CertificateSha256.Length > 0)
                client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
                    certificate is not null && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 &&
                    certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(endpoint.CertificateSha256, StringComparison.OrdinalIgnoreCase) &&
                    DateTime.TryParse(certificate.GetEffectiveDateString(), out var effective) && effective.ToUniversalTime() <= DateTime.UtcNow &&
                    DateTime.TryParse(certificate.GetExpirationDateString(), out var expiry) && expiry.ToUniversalTime() > DateTime.UtcNow;
            await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Tls == MailTls.Implicit ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, timeout.Token);
            // RFC 2971 allows ID before or after authentication. Advertise the actual client only.
            var imap = preset?.IdentifyClient == true ? client as ImapClient : null;
            var identification = new ImapImplementation { Name = "MyMailBox",
                Version = typeof(ProtocolMailService).Assembly.GetName().Version?.ToString(3) };
            bool identified = imap?.Capabilities.HasFlag(ImapCapabilities.Id) == true;
            if (identified) await imap!.IdentifyAsync(identification,timeout.Token);
            var secret = Encoding.UTF8.GetString(plain);
            if (endpoint.Authentication == MailAuthentication.OAuth2) await client.AuthenticateAsync(new SaslMechanismOAuth2(endpoint.User, secret), timeout.Token);
            else await client.AuthenticateAsync(endpoint.User, secret, timeout.Token);
            if (!identified && imap?.Capabilities.HasFlag(ImapCapabilities.Id) == true)
                await imap.IdentifyAsync(identification,timeout.Token);
            command.CommandText = "UPDATE accounts SET auth_state=$state WHERE id=$id AND enabled=1 AND removed=0";
            command.Parameters.AddWithValue("$state", outgoing ? "发信连接已验证" : "收信连接已验证"); command.ExecuteNonQuery();
        }
        catch (MailKit.Security.AuthenticationException) when (preset is not null)
        {
            await _accounts.AuthenticationFailed(id);
            throw new InvalidDataException("认证失败。" + preset.Guidance);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("连接或认证超时。"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return endpoint;
    }
    // Do not return remote exception text: servers can echo authentication data or message contents.
    private async Task<T> ReadOperation<T>(Guid id, Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (MailKit.Security.AuthenticationException) { await _accounts.AuthenticationFailed(id); throw new InvalidDataException("认证失败，请核对用户名、密码／授权码或更新访问令牌后重试。"); }
        catch (SslHandshakeException) { throw new InvalidDataException("TLS 验证失败，请检查服务器名称、端口与证书；不会忽略证书错误。"); }
        catch (NotSupportedException) { throw new InvalidDataException("服务器不支持所选加密或认证方式；不会降级为明文连接，请核对 TLS 端口和认证配置。"); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            var failure = new InvalidDataException("服务器连接中断，请检查网络、代理和服务器配置后重试。");
            failure.Data["receive-offline"] = true;
            throw failure;
        }
        catch (ProtocolException) { throw new InvalidDataException("服务器连接中断或响应格式异常，请重新连接后重试。"); }
        catch (CommandException)
        {
            var settings = await Settings(id);
            var preset = settings.PresetId is null ? null : _presets().FirstOrDefault(p=>p.Id==settings.PresetId);
            throw new InvalidDataException(preset is null ? "服务器请求失败，请检查服务器配置后重试。" : "服务器拒绝了当前操作。"+preset.Guidance);
        }
    }
    public Task<IReadOnlyList<RemoteFolder>> Folders(Guid account, CancellationToken cancellation = default) => ReadOperation<IReadOnlyList<RemoteFolder>>(account, async () =>
    {
        var identity = await Account(account, AccountCapabilities.Receive | AccountCapabilities.Folders, cancellation);
        if (identity.Provider is not ("imap" or "imap-smtp")) throw new InvalidDataException("此账户不是 IMAP 账户。");
        using var client = _imapClients(); await Connect(client, account, false, cancellation);
        var folders = new List<IMailFolder> { client.Inbox };
        foreach (var ns in client.PersonalNamespaces) folders.AddRange(await client.GetFoldersAsync(ns, cancellationToken: cancellation));
        var result = folders.DistinctBy(f => f.FullName).Select(f => new RemoteFolder(f.FullName, f.Name, Role(f.Attributes), !f.Attributes.HasFlag(FolderAttributes.NoSelect))).ToArray();
        await client.DisconnectAsync(true, cancellation); return result;
    });
    private static string Role(FolderAttributes flags) => flags.HasFlag(FolderAttributes.Inbox) ? "inbox" : flags.HasFlag(FolderAttributes.Sent) ? "sent" : flags.HasFlag(FolderAttributes.Drafts) ? "drafts" : flags.HasFlag(FolderAttributes.Trash) ? "trash" : flags.HasFlag(FolderAttributes.Junk) ? "junk" : flags.HasFlag(FolderAttributes.Archive) ? "archive" : "custom";
    public Task<RemoteMailPage> Summaries(Guid account, string folder, int offset, int limit, CancellationToken cancellation = default) => ReadOperation(account, async () =>
    {
        await Account(account, AccountCapabilities.Receive | AccountCapabilities.Folders, cancellation);
        if (offset < 0 || limit is < 1 or > 200) throw new InvalidDataException("分页参数无效。");
        using var client = _imapClients(); await Connect(client, account, false, cancellation);
        var selected = await client.GetFolderAsync(folder, cancellation); await selected.OpenAsync(FolderAccess.ReadOnly, cancellation);
        var end = selected.Count - offset - 1;
        if (end < 0) return new RemoteMailPage([], offset, false, selected.UidValidity);
        var start = Math.Max(0, end - limit + 1);
        var summaries = await selected.FetchAsync(start, end, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Size | MessageSummaryItems.Flags, cancellation);
        var result = summaries.OrderByDescending(s => s.Index).Select(s => new RemoteSummary(s.UniqueId.Id, s.Envelope?.Subject ?? "（无主题）", s.Envelope?.From.ToString() ?? "", s.Envelope?.Date, s.Size ?? 0, s.Flags?.HasFlag(MessageFlags.Seen) == true)).ToArray();
        await client.DisconnectAsync(true, cancellation); return new RemoteMailPage(result, offset, start > 0, selected.UidValidity);
    });
}
