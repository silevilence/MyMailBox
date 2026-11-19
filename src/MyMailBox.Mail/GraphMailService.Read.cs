using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class GraphMailService
{
    private const string Root = "https://graph.microsoft.com/v1.0/me/";
    private static string Id(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 8192
        ? Uri.EscapeDataString(value) : throw new InvalidDataException("远程目录或邮件 ID 无效，请刷新列表。");
    public static Uri ValidatePage(string url, string expectedPath)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != expectedPath)
            throw new InvalidDataException("微软返回了不属于当前目录的分页链接，已停止请求。");
        return uri;
    }
    public Task<GraphFolders> Folders(Guid account, string? parent = null, string? next = null, CancellationToken cancellation = default) => Run(async () =>
    {
        var route = Root + (parent is null ? "mailFolders" : "mailFolders/" + Id(parent) + "/childFolders");
        using var data = await Json(account, next ?? route + "?$top=50&$select=id,displayName,childFolderCount", new Uri(route).AbsolutePath, cancellation);
        var items = data.RootElement.GetProperty("value").EnumerateArray().Select(f => new GraphFolder(Required(f, "id"), Required(f, "displayName"), f.GetProperty("childFolderCount").GetInt32())).ToArray();
        return new GraphFolders(items, Next(data, new Uri(route).AbsolutePath));
    }, cancellation);
    public Task<GraphMessages> Messages(Guid account, string folder, string? next = null, CancellationToken cancellation = default) =>
        Messages(account,folder,next,25,cancellation);
    internal Task<GraphMessages> Messages(Guid account, string folder, string? next, int limit, CancellationToken cancellation) => Run(async () =>
    {
        var route = Root + "mailFolders/" + Id(folder) + "/messages";
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var data = await Json(account, next ?? route + $"?$top={limit}&$select=id,subject,from,receivedDateTime,isRead,hasAttachments&$orderby=receivedDateTime%20desc", new Uri(route).AbsolutePath, cancellation);
        var items = data.RootElement.GetProperty("value").EnumerateArray().Select(m => new GraphSummary(Required(m, "id"), m.GetProperty("subject").GetString() ?? "", Sender(m),
            m.TryGetProperty("receivedDateTime", out var date) && DateTimeOffset.TryParse(date.GetString(), out var parsed) ? parsed : null,
            m.GetProperty("isRead").GetBoolean(), m.GetProperty("hasAttachments").GetBoolean())).ToArray();
        return new GraphMessages(items, Next(data, new Uri(route).AbsolutePath));
    }, cancellation);
    private static string Required(JsonElement item, string key) => item.GetProperty(key).GetString() is { Length: > 0 } text ? text : throw new InvalidDataException("微软响应缺少目录或邮件标识。");
    internal static string Sender(JsonElement message) => message.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object && from.TryGetProperty("emailAddress", out var email) && email.ValueKind == JsonValueKind.Object
        ? (email.TryGetProperty("address", out var address) ? address.GetString() ?? "" : "") : "";
    private static string? Next(JsonDocument document, string path) => document.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.GetString() is { Length: > 0 } value ? ValidatePage(value, path).AbsoluteUri : null;
    private async Task<JsonDocument> Json(Guid account, string url, string path, CancellationToken token) => JsonDocument.Parse(await Get(account, ValidatePage(url, path), 4 * 1024 * 1024, token));
    private async Task<byte[]> Get(Guid account, Uri uri, int maxBytes, CancellationToken token, bool delta = false)
    {
        var identity = await Account(account, token); var config = Configuration(account);
        if (config.Home is null) throw new InvalidDataException("尚未连接微软账户，请先登录授权。");
        var sync = new GraphDeltaStore(_store, account, config.Home);
        sync.CheckCooldown();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var grant = await _auth.Acquire(account, config.Client, config.Home, false, attempt > 0, timeout.Token);
            if (grant.HomeAccountId != config.Home || !grant.Username.Equals(identity.Address, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("授权身份不匹配，请重新登录当前账户。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
            request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (delta && await Expired(response, timeout.Token)) throw new DeltaExpired();
            if (delta && response.StatusCode == HttpStatusCode.NotFound) throw new DeltaFolderMissing();
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidDataException("微软授权已失效，请重新登录授权。");
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidDataException("没有读取权限；请检查应用的 Mail.Read 委托权限并重新授权。");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("远程邮件或目录已不存在，请刷新列表。");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                if (retry != null || delta)
                {
                    var until = retry?.Date ?? DateTimeOffset.UtcNow.Add(retry?.Delta ?? TimeSpan.FromSeconds(30));
                    sync.Cooldown(until);
                }
                throw new InvalidDataException("微软服务正在限流，请稍后重试。");
            }
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("微软邮件请求失败，请稍后重试；没有更改云端邮件。");
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("远程内容超过本次读取大小限制。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token); using var output = new MemoryStream();
            var buffer = new byte[81920]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > maxBytes) throw new InvalidDataException("远程内容超过本次读取大小限制。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        throw new InvalidOperationException("Unreachable Graph retry state.");
    }
    public Task<Guid> Download(Guid account, string message, CancellationToken cancellation = default) => Run(async () =>
    {
        await Account(account, cancellation); var config = Configuration(account);
        if (config.Home is null) throw new InvalidDataException("尚未连接微软账户，请先登录授权。");
        using (var cache = _store.Open())
        using (var lookup = cache.CreateCommand())
        {
            lookup.CommandText = "SELECT messages.id FROM messages JOIN graph_receipts ON messages.id=message_id WHERE account_id=$a AND home_id=$h AND remote_id=$r";
            lookup.Parameters.AddWithValue("$a", account.ToString()); lookup.Parameters.AddWithValue("$h", config.Home); lookup.Parameters.AddWithValue("$r", message);
            if (lookup.ExecuteScalar() is string saved) return Guid.Parse(saved);
        }
        var raw = await Get(account, new Uri(Root + "messages/" + Id(message) + "/$value"), MimeReader.MaxMessageBytes, cancellation);
        var parsed = await new MimeReader().Parse(raw, cancellation);
        using var db = _store.Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT messages.id FROM messages JOIN graph_receipts ON messages.id=message_id WHERE account_id=$account AND home_id=$home AND remote_id=$remote";
        command.Parameters.AddWithValue("$account", account.ToString()); command.Parameters.AddWithValue("$home", config.Home); command.Parameters.AddWithValue("$remote", message);
        if (command.ExecuteScalar() is string existing) { tx.Commit(); return Guid.Parse(existing); }
        command.CommandText = "SELECT id FROM folders WHERE account_id=$account AND role='graph-downloads'";
        var folder = command.ExecuteScalar() as string ?? Guid.NewGuid().ToString();
        var id = Guid.NewGuid();
        command.CommandText = """
            INSERT OR IGNORE INTO folders VALUES($folder,$account,'Outlook.com 已下载','Outlook.com / 已下载','graph-downloads',NULL);
            INSERT INTO messages(id,folder_id,subject,sender,recipient,date,size,raw,sha256) VALUES($id,$folder,$subject,$from,$to,$date,$size,$raw,$hash);
            INSERT INTO graph_receipts VALUES($account,$home,$remote,$id) ON CONFLICT(account_id,home_id,remote_id) DO UPDATE SET message_id=excluded.message_id;
            """;
        command.Parameters.AddWithValue("$folder", folder); command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$subject", parsed.Subject); command.Parameters.AddWithValue("$from", parsed.From); command.Parameters.AddWithValue("$to", parsed.To);
        command.Parameters.AddWithValue("$date", parsed.Date.ToUniversalTime().ToString("O")); command.Parameters.AddWithValue("$size", raw.Length);
        command.Parameters.AddWithValue("$raw", raw); command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(raw)));
        cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); cancellation.ThrowIfCancellationRequested(); tx.Commit(); return id;
    }, cancellation);
}
