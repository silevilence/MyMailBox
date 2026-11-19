using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class GraphMailService
{
    // Single-request MIME transport. The bound applies to the complete base64 HTTP body.
    internal static void CheckMimeSize(long rawBytes)
    {
        if ((rawBytes + 2) / 3 * 4 > 4 * 1024 * 1024)
            throw new InvalidDataException("本应用的 Graph 单次提交上限为 4 MiB（含 MIME 和 Base64 编码）。请缩小附件。");
    }

    public Task Enable(Guid account, CancellationToken token) => Run(async () =>
    {
        var grant = await WriteGrant(account, true, token);
        using var db = _store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE accounts SET capabilities=capabilities | $send,auth_state='微软收发与整理已授权'
            WHERE id=$a AND enabled=1 AND removed=0 AND address=$address COLLATE NOCASE
              AND EXISTS(SELECT 1 FROM graph_settings WHERE account_id=$a AND home_id=$home AND client_id=$client)
            """;
        cmd.Parameters.AddWithValue("$a", account.ToString());
        cmd.Parameters.AddWithValue("$home", grant.HomeAccountId);
        cmd.Parameters.AddWithValue("$address", grant.Username);
        cmd.Parameters.AddWithValue("$client", _clientId);
        cmd.Parameters.AddWithValue("$send", (int)AccountCapabilities.Send);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("账户已变化，请重新连接。");
    }, token);

    private async Task<GraphGrant> WriteGrant(Guid id, bool interactive, CancellationToken token)
    {
        var account = await Account(id, token);
        var config = Configuration(id);
        if (config.Home == null) throw new InvalidDataException("请先登录对应的微软账户。");
        if (!interactive && !account.Capabilities.HasFlag(AccountCapabilities.Send))
            throw new InvalidDataException("请先在 Outlook.com 邮件页授权发信与整理。");
        if (_auth is not IGraphWriteAuthorization auth) throw new InvalidDataException("此授权组件只支持读取。");
        new GraphDeltaStore(_store, id, config.Home).CheckCooldown();
        var grant = await auth.AcquireWrite(id, config.Client, config.Home, interactive, token);
        if (grant.HomeAccountId != config.Home || !grant.Username.Equals(account.Address, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("授权身份不匹配，请选择当前邮箱账户。");
        return grant;
    }

    private async Task<HttpResponseMessage> RequestWrite(Guid account, GraphGrant grant, HttpMethod method, string route, HttpContent content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, Root + route) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await _http.SendAsync(request, timeout.Token);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                new GraphDeltaStore(_store, account, grant.HomeAccountId).Cooldown(retry?.Date ?? DateTimeOffset.UtcNow.Add(retry?.Delta ?? TimeSpan.FromSeconds(30)));
            }
            throw new GraphWriteFailure(response.StatusCode);
        }
    }

    private sealed class GraphWriteFailure(HttpStatusCode status) : IOException(status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "微软拒绝写入，请重新授权发信与整理。",
        HttpStatusCode.RequestEntityTooLarge => "微软拒绝了过大的邮件，请缩小内容或附件。",
        HttpStatusCode.NotFound => "微软未找到邮件或目标目录，请刷新。",
        HttpStatusCode.TooManyRequests => "微软服务正在限流，请稍后重试。",
        _ => $"微软邮件操作返回 HTTP {(int)status}，请核对云端结果。"
    })
    {
        internal bool Refused => (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout;
    }

    private static StringContent MimeContent(byte[] raw) => new(Convert.ToBase64String(raw), Encoding.ASCII, "text/plain");

    internal Task<SendOutcome> SendQueued(Guid account, byte[] raw, string[] recipients, Func<bool> claim, CancellationToken token) => Run(async () =>
    {
        CheckMimeSize(raw.Length);
        var grant = await WriteGrant(account, false, token);
        token.ThrowIfCancellationRequested();
        if (!claim()) throw new InvalidDataException("发件记录已取消或已提交。");
        try
        {
            using var response = await RequestWrite(account, grant, HttpMethod.Post, "sendMail", MimeContent(raw), token);
            if (response.StatusCode == HttpStatusCode.Accepted)
                return new SendOutcome("sent", "微软已受理发送请求，最终投递结果请查看邮箱。", recipients, []);
        }
        catch (GraphWriteFailure e) when (e.Refused) { return new SendOutcome("rejected", e.Message, [], recipients); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { }
        return new SendOutcome("unknown", "提交后未取得明确回执，请先核对已发送邮件。系统不会自动重发。", [], []);
    }, token);

    public Task MarkRead(Guid account, string message, bool read, CancellationToken token) => Run(async () =>
    {
        var grant = await WriteGrant(account, false, token);
        using var response = await RequestWrite(account, grant, HttpMethod.Patch, "messages/" + Id(message), JsonContent.Create(new { isRead = read }), token);
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE graph_items SET data=json_set(data,'$.IsRead',json($read)) WHERE account=$a AND home=$h AND id=$m";
        cmd.Parameters.AddWithValue("$a", account.ToString()); cmd.Parameters.AddWithValue("$h", grant.HomeAccountId);
        cmd.Parameters.AddWithValue("$m", message); cmd.Parameters.AddWithValue("$read", read ? "true" : "false");
        cmd.ExecuteNonQuery();
    }, token);

    public Task<string> Move(Guid account, string message, string destination, CancellationToken token) => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new InvalidDataException("请选择本账户的目标目录。");
        var grant = await WriteGrant(account, false, token);
        using var response = await RequestWrite(account, grant, HttpMethod.Post, "messages/" + Id(message) + "/move", JsonContent.Create(new { destinationId = destination }), token);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        var moved = Required(payload.RootElement, "id");
        using var db = _store.Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE graph_receipts SET remote_id=$new WHERE account_id=$a AND home_id=$h AND remote_id=$old;
            INSERT OR REPLACE INTO graph_items(account,home,folder,id,data,date,generation)
            SELECT i.account,i.home,$dest,$new,json_set(i.data,'$.Id',$new),i.date,s.generation FROM graph_items i
            JOIN graph_scopes s ON s.account=i.account AND s.home=i.home AND s.folder=$dest
            WHERE i.account=$a AND i.home=$h AND i.id=$old LIMIT 1;
            DELETE FROM graph_items WHERE account=$a AND home=$h AND id=$old AND (folder<>$dest OR id<>$new);
            """;
        cmd.Parameters.AddWithValue("$a", account.ToString()); cmd.Parameters.AddWithValue("$h", grant.HomeAccountId);
        cmd.Parameters.AddWithValue("$old", message); cmd.Parameters.AddWithValue("$new", moved); cmd.Parameters.AddWithValue("$dest", destination);
        cmd.ExecuteNonQuery(); tx.Commit();
        return moved;
    }, token);

    public Task<string> SaveDraft(MailDraft draft, CancellationToken token) => Run(async () =>
    {
        var grant = await WriteGrant(draft.Account, false, token);
        var drafts = new MailDraftStore(_store);
        var saved = drafts.List(draft.Account).SingleOrDefault(d => d.Id == draft.Id && d.Version == draft.Version)
            ?? throw new InvalidDataException("本地草稿已变化，请先保存当前版本。");
        var (raw, _) = await drafts.Outgoing(saved.Id, await Account(saved.Account, token), saved.Message(), null, true, token);
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$a", saved.Account.ToString()); cmd.Parameters.AddWithValue("$h", grant.HomeAccountId);
        cmd.Parameters.AddWithValue("$d", saved.Id.ToString()); cmd.Parameters.AddWithValue("$v", saved.Version);
        const string key = "account=$a AND home=$h AND draft=$d AND version=$v";
        cmd.CommandText = "INSERT OR IGNORE INTO graph_draft_copies VALUES($a,$h,$d,$v,NULL)";
        if (cmd.ExecuteNonQuery() == 0)
        {
            cmd.CommandText = "SELECT remote FROM graph_draft_copies WHERE " + key;
            return cmd.ExecuteScalar() as string ?? throw new InvalidDataException("此版本已尝试保存，请先核对云端草稿。");
        }
        try
        {
            using var response = await RequestWrite(saved.Account, grant, HttpMethod.Post, "messages", MimeContent(raw), token);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            var remote = Required(doc.RootElement, "id");
            cmd.CommandText = "UPDATE graph_draft_copies SET remote=$id WHERE " + key;
            cmd.Parameters.AddWithValue("$id", remote); cmd.ExecuteNonQuery(); return remote;
        }
        catch (GraphWriteFailure e) when (e.Refused)
        {
            cmd.CommandText = "DELETE FROM graph_draft_copies WHERE " + key; cmd.ExecuteNonQuery(); throw;
        }
    }, token);
}
