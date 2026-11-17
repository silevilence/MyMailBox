using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class RemoteContentService : IRemoteContentService, IDisposable
{
    public const int MaxResourceBytes = 10 * 1024 * 1024;
    private readonly SqliteMailStore _store;
    private readonly HttpClient _client;
    public RemoteContentService(SqliteMailStore store, HttpMessageHandler? handler = null)
    {
        _store = store; _client = new HttpClient(handler ?? SafeHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        using var db = store.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS remote_rules(scope INTEGER NOT NULL,value TEXT NOT NULL,PRIMARY KEY(scope,value))"; command.ExecuteNonQuery();
    }
    public Task<RemoteContentPreview> Preview(RemoteContentContext context, CancellationToken cancellation = default) => Task.Run(() =>
    {
        var (sender, domain) = Address(context.From); var rules = new List<RemoteContentRule>();
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT scope,value FROM remote_rules";
        using var reader = command.ExecuteReader(); while (reader.Read())
        {
            cancellation.ThrowIfCancellationRequested(); var scope = (RemoteContentScope)reader.GetInt32(0); var value = reader.GetString(1);
            if (value == Key(context, scope, sender, domain)) rules.Add(new(scope, value));
        }
        return new RemoteContentPreview(sender, domain, rules.ToArray());
    }, cancellation);
    public Task SetRule(RemoteContentContext context, RemoteContentScope scope, bool allow, CancellationToken cancellation = default) => Task.Run(() =>
    {
        var (sender, domain) = Address(context.From); var value = Key(context, scope, sender, domain);
        if (value.Length == 0) throw new InvalidDataException("发件地址不唯一或无效，不能建立发件人/域规则；可仅放行本封。");
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = allow ? "INSERT OR IGNORE INTO remote_rules VALUES($scope,$value)" : "DELETE FROM remote_rules WHERE scope=$scope AND value=$value";
        command.Parameters.AddWithValue("$scope", (int)scope); command.Parameters.AddWithValue("$value", value); cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
    }, cancellation);
    public async Task<RemoteLoadResult> Load(RemoteContentContext context, IReadOnlyList<RemoteMailResource> resources, CancellationToken cancellation = default)
    {
        if ((await Preview(context, cancellation)).MatchingRules.Length == 0) return new(new Dictionary<string, MailResource>(), ["远程内容仍被阻止，请先选择放行范围。"]);
        var loaded = new Dictionary<string, MailResource>(); var results = new List<string>(); var total = 0;
        foreach (var resource in resources.Take(30))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不加载本机、内网或带凭据的资源地址。");
                var cacheKey = "remote:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
                var cached = await ReadCache(cacheKey, cancellation);
                MailResource content;
                if (cached is not null && DateTimeOffset.UtcNow - cached.Stored < TimeSpan.FromDays(1)) content = cached.Resource with { Key = resource.Id };
                else
                {
                    using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
                    if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("资源重定向已阻止，未向跳转目标发送请求。");
                    response.EnsureSuccessStatusCode();
                    var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                    if (!HtmlMailRenderer.AllowedType(resource.Kind, type)) throw new InvalidDataException("资源类型不受支持，不会作为脚本或网页执行。");
                    if (response.Content.Headers.ContentLength > MaxResourceBytes) throw new InvalidDataException("资源超过 10 MiB 限制。");
                    await using var input = await response.Content.ReadAsStreamAsync(cancellation); using var output = new MemoryStream(); var buffer = new byte[81920]; int read;
                    while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                    {
                        if (output.Length + read > MaxResourceBytes) throw new InvalidDataException("资源超过 10 MiB 限制。");
                        output.Write(buffer, 0, read);
                    }
                    content = new(resource.Id, type, output.ToArray()); await WriteCache(cacheKey, new(DateTimeOffset.UtcNow, content), cancellation);
                }
                if (!HtmlMailRenderer.AllowedType(resource.Kind, content.ContentType)) throw new InvalidDataException("缓存资源类型不匹配。");
                if (total + content.Data.Length > 30 * 1024 * 1024) { results.Add("本封加载量达到 30 MiB 限制，其余内容仍阻止。"); break; }
                total += content.Data.Length; loaded.Add(resource.Id, content); results.Add(uri.Host + (cached is null ? "：已加载" : "：使用本地缓存"));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { results.Add("资源加载超时，可稍后重试。"); }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or SocketException) { results.Add("资源未加载：" + error.Message); }
        }
        if (resources.Count > 30) results.Add("仅加载前 30 项，其余内容保持阻止。");
        return new(loaded, results.ToArray());
    }
    private Task<CachedResource?> ReadCache(string key, CancellationToken cancellation) => Task.Run(() =>
    {
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT content FROM disposable_cache WHERE id=$id"; command.Parameters.AddWithValue("$id", key);
        return command.ExecuteScalar() is byte[] bytes ? JsonSerializer.Deserialize<CachedResource>(bytes) : null;
    }, cancellation);
    private Task WriteCache(string key, CachedResource value, CancellationToken cancellation) => Task.Run(() =>
    {
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT INTO disposable_cache VALUES($id,$content) ON CONFLICT(id) DO UPDATE SET content=excluded.content";
        command.Parameters.AddWithValue("$id", key); command.Parameters.AddWithValue("$content", JsonSerializer.SerializeToUtf8Bytes(value)); cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
    }, cancellation);
    private static (string Sender, string Domain) Address(string value)
    {
        if (!InternetAddressList.TryParse(value, out var addresses)) return ("", "");
        var mailboxes = addresses.Mailboxes.ToArray(); if (mailboxes.Length != 1) return ("", "");
        var address = mailboxes[0].Address; var at = address.LastIndexOf('@'); if (at <= 0) return ("", "");
        try { var domain = new System.Globalization.IdnMapping().GetAscii(address[(at + 1)..]).ToLowerInvariant(); return (address[..at].ToLowerInvariant() + "@" + domain, domain); } catch (ArgumentException) { return ("", ""); }
    }
    private static string Key(RemoteContentContext context, RemoteContentScope scope, string sender, string domain) => scope switch { RemoteContentScope.Message => context.MessageId.ToString(), RemoteContentScope.Sender => sender, RemoteContentScope.Domain => domain, _ => throw new ArgumentOutOfRangeException(nameof(scope)) };
    private static SocketsHttpHandler SafeHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (context, cancellation) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
            var address = addresses.FirstOrDefault(IsPublicAddress) ?? throw new HttpRequestException("资源地址不是公网地址，已阻止。");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    };
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        var b = address.GetAddressBytes();
        return b.Length == 4 ? b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127) : (b[0] & 0xfe) != 0xfc;
    }
    public void Dispose() => _client.Dispose();
    private sealed record CachedResource(DateTimeOffset Stored, MailResource Resource);
}
