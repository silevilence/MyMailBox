using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace MyMailBox.Mail;

public sealed record GraphGrant(string AccessToken, string HomeAccountId, string Username);
public interface IGraphAuthorization
{
    Task<GraphGrant> Acquire(Guid account, string clientId, string? homeAccountId, bool interactive, bool refresh, CancellationToken cancellation);
}

/// <summary>One protected MSAL cache per local account; portable backups already omit credentials.</summary>
public sealed class GraphTokenCache(SqliteMailStore store)
{
    public byte[]? Load(Guid account)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT protected FROM credentials JOIN graph_settings ON credentials.id=cache_ref JOIN accounts ON accounts.id=account_id WHERE account_id=$id AND removed=0 AND enabled=1";
        command.Parameters.AddWithValue("$id", account.ToString());
        return command.ExecuteScalar() is byte[] encrypted ? ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser) : null;
    }
    public void Save(Guid account, byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO credentials SELECT cache_ref,$value FROM graph_settings JOIN accounts ON accounts.id=account_id WHERE account_id=$id AND removed=0 AND enabled=1 ON CONFLICT(id) DO UPDATE SET protected=excluded.protected";
        command.Parameters.AddWithValue("$id", account.ToString()); command.Parameters.AddWithValue("$value", encrypted);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("账户已停用或移除，请重新选择账户。");
    }
}

public interface IGraphWriteAuthorization
{
    Task<GraphGrant> AcquireWrite(Guid account, string clientId, string homeAccountId, bool interactive, CancellationToken token);
}

public sealed class GraphAuthorization(GraphTokenCache cache) : IGraphAuthorization, IGraphWriteAuthorization
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/Mail.Read"];
    private static readonly string[] WriteScopes = ["https://graph.microsoft.com/Mail.ReadWrite", "https://graph.microsoft.com/Mail.Send"];
    public Task<GraphGrant> AcquireWrite(Guid account, string clientId, string homeAccountId, bool interactive, CancellationToken token) =>
        AcquireScopes(account, clientId, homeAccountId, interactive, false, WriteScopes, token);
    public Task<GraphGrant> Acquire(Guid account, string clientId, string? homeAccountId, bool interactive, bool refresh, CancellationToken cancellation) =>
        AcquireScopes(account, clientId, homeAccountId, interactive, refresh, Scopes, cancellation);
    private async Task<GraphGrant> AcquireScopes(Guid account, string clientId, string? homeAccountId, bool interactive, bool refresh, string[] scopes, CancellationToken cancellation)
    {
        // Consumers prevents accidentally validating an organizational account in task 32.
        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority("https://login.microsoftonline.com/consumers")
            .WithRedirectUri("http://localhost").Build();
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            var bytes = cache.Load(account);
            if (bytes is null) return;
            try { args.TokenCache.DeserializeMsalV3(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            var bytes = args.TokenCache.SerializeMsalV3();
            try { cache.Save(account, bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
        AuthenticationResult result;
        if (interactive)
            result = await app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync(cancellation);
        else
        {
            var known = (await app.GetAccountsAsync()).SingleOrDefault(a => a.HomeAccountId.Identifier == homeAccountId);
            if (known is null) throw new InvalidDataException("需要重新授权，请点击登录微软账户。");
            result = await app.AcquireTokenSilent(scopes, known).WithForceRefresh(refresh).ExecuteAsync(cancellation);
        }
        return new(result.AccessToken, result.Account.HomeAccountId.Identifier, result.Account.Username);
    }
}
