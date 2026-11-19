using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Identity.Client;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

/// <summary>Reading and remote writes use separately requested delegated grants.</summary>
public sealed partial class GraphMailService : IGraphMailService, IGraphFolderSync, IGraphWrites, IDisposable
{
    private readonly SqliteMailStore _store;
    private readonly AccountService _accounts;
    private readonly IGraphAuthorization _auth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _clientId;
    private readonly SemaphoreSlim _gate = new(1);
    public GraphMailService(SqliteMailStore store, IGraphAuthorization? authorization = null, HttpClient? http = null, string? clientId = null)
    {
        clientId ??= GraphApplication.ClientId;
        if (clientId.Length > 0 && (!Guid.TryParse(clientId, out var applicationId) || applicationId == Guid.Empty)) throw new ArgumentException("应用配置中的 Client ID 无效。", nameof(clientId));
        _clientId = clientId.Length == 0 ? "" : Guid.Parse(clientId).ToString();
        _store = store; _accounts = new(store); _auth = authorization ?? new GraphAuthorization(new(store));
        _http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }); _ownsHttp = http is null;
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS graph_settings(account_id TEXT PRIMARY KEY REFERENCES accounts(id),client_id TEXT NOT NULL,home_id TEXT,cache_ref TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS graph_draft_copies(account TEXT,home TEXT,draft TEXT,version INTEGER,remote TEXT,PRIMARY KEY(account,home,draft,version));
            CREATE TABLE IF NOT EXISTS graph_receipts(account_id TEXT NOT NULL,home_id TEXT NOT NULL,remote_id TEXT NOT NULL,message_id TEXT NOT NULL,PRIMARY KEY(account_id,home_id,remote_id));
            CREATE TRIGGER IF NOT EXISTS remove_graph_credentials AFTER UPDATE OF removed ON accounts WHEN NEW.removed=1 BEGIN
                DELETE FROM credentials WHERE id IN(SELECT cache_ref FROM graph_settings WHERE account_id=NEW.id);
                DELETE FROM graph_settings WHERE account_id=NEW.id;
            END;
            """; command.ExecuteNonQuery();
        GraphDeltaStore.Initialize(db);
    }
    private async Task<MailAccount> Account(Guid id, CancellationToken token)
    {
        var account = (await _accounts.List(token)).SingleOrDefault(a => a.Id == id && a.Enabled && a.Provider == "outlook-com");
        return account ?? throw new InvalidDataException("请选择启用的 Outlook.com 个人账户。");
    }
    private (string Client, string? Home) Configuration(Guid account)
    {
        using var db = _store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT client_id,home_id FROM graph_settings WHERE account_id=$id"; command.Parameters.AddWithValue("$id", account.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (_clientId, null);
        var client = reader.GetString(0);
        return (client, client == _clientId && !reader.IsDBNull(1) ? reader.GetString(1) : null);
    }
    public async Task<GraphSettings> Settings(Guid account, CancellationToken cancellation = default)
    {
        var identity = await Account(account, cancellation); var config = Configuration(account);
        var cache = new GraphTokenCache(_store).Load(account);
        try { return new(config.Home is not null && cache is not null) { CanWrite = config.Home != null && cache != null && identity.Capabilities.HasFlag(AccountCapabilities.Send) }; }
        finally { if (cache is not null) CryptographicOperations.ZeroMemory(cache); }
    }
    private void ConfigureAccount(Guid account, CancellationToken cancellation)
    {
        using var db = _store.Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT client_id FROM graph_settings WHERE account_id=$id;
            """;
        command.Parameters.AddWithValue("$id", account.ToString());
        if (command.ExecuteScalar() is string existing && existing == _clientId) return;
        command.CommandText = """
            DELETE FROM credentials WHERE id IN(SELECT cache_ref FROM graph_settings WHERE account_id=$id);
            INSERT INTO graph_settings VALUES($id,$client,NULL,$cache) ON CONFLICT(account_id) DO UPDATE SET client_id=excluded.client_id,home_id=NULL,cache_ref=excluded.cache_ref;
            UPDATE accounts SET capabilities=capabilities & ~$send,auth_state='需要微软授权' WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$send", (int)AccountCapabilities.Send);
        command.Parameters.AddWithValue("$client", _clientId); command.Parameters.AddWithValue("$cache", Guid.NewGuid().ToString());
        cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); tx.Commit();
    }
    public Task SignIn(Guid account, CancellationToken cancellation = default) => Run(async () =>
    {
        if (_clientId.Length == 0) throw new InvalidDataException("此版本尚未配置微软登录，请联系应用维护者。");
        var identity = await Account(account, cancellation); ConfigureAccount(account, cancellation); var config = Configuration(account);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var grant = await _auth.Acquire(account, config.Client, config.Home, true, false, timeout.Token);
        if (!grant.Username.Equals(identity.Address, StringComparison.OrdinalIgnoreCase) || config.Home is not null && grant.HomeAccountId != config.Home)
        {
            Forget(account); throw new InvalidDataException("登录账户与当前邮箱身份不一致，未连接；请使用对应个人账户，或新建其登录地址对应的账户。");
        }
        using var db = _store.Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE graph_settings SET home_id=$home WHERE account_id=$id AND EXISTS(SELECT 1 FROM accounts WHERE id=$id AND enabled=1 AND removed=0); UPDATE accounts SET auth_state='微软只读授权已完成' WHERE id=$id AND enabled=1 AND removed=0";
        command.Parameters.AddWithValue("$id", account.ToString()); command.Parameters.AddWithValue("$home", grant.HomeAccountId); cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
    }, cancellation);
    public Task Disconnect(Guid account, CancellationToken cancellation = default) => Run(async () => { await Account(account, cancellation); Forget(account); }, cancellation);
    private void Forget(Guid account)
    {
        using var db = _store.Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM credentials WHERE id IN(SELECT cache_ref FROM graph_settings WHERE account_id=$id); UPDATE graph_settings SET home_id=NULL WHERE account_id=$id; UPDATE accounts SET capabilities=capabilities & ~$send,auth_state='需要微软授权' WHERE id=$id";
        command.Parameters.AddWithValue("$send", (int)AccountCapabilities.Send);
        command.Parameters.AddWithValue("$id", account.ToString()); command.ExecuteNonQuery();
    }
    private async Task Run(Func<Task> operation, CancellationToken token) => await Run(async () => { await operation(); return true; }, token);
    private async Task<T> Run<T>(Func<Task<T>> operation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await operation(); }
        catch (GraphWriteFailure e) { throw new InvalidDataException(e.Message, e); }
        catch (MsalUiRequiredException) { throw new InvalidDataException("微软授权已过期或被撤销，请重新登录授权。"); }
        catch (MsalClientException e) when (e.ErrorCode == "authentication_canceled") { throw new InvalidDataException("已取消微软登录，可以重新尝试。"); }
        catch (MsalException) { throw new InvalidDataException("微软授权未完成，请检查应用注册、个人账户支持及网络，然后重试。"); }
        catch (HttpRequestException) { throw new InvalidDataException("无法连接微软邮件服务，请检查网络后重试。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidDataException("微软连接或登录超时，可以重试。"); }
        catch (JsonException) { throw new InvalidDataException("微软邮件服务响应格式无效，请稍后重试。"); }
        finally { _gate.Release(); }
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); _gate.Dispose(); }
}
