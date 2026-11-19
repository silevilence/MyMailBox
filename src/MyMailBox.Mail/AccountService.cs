using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class AccountService : IAccountService
{
    private readonly SqliteMailStore _store;
    private readonly Func<IReadOnlyList<IMailboxProvider>> _providers;
    public IReadOnlyList<MailboxType> ProviderTypes => _providers().Select(p => p.Type).Where(t => t.Id.Contains('/')).ToArray();
    public AccountService(SqliteMailStore store) : this(store, () => []) { }
    public AccountService(SqliteMailStore store, Func<IReadOnlyList<IMailboxProvider>> providers)
    {
        _providers = providers;
        _store = store;
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY, name TEXT NOT NULL, provider TEXT NOT NULL, address TEXT NOT NULL, alias TEXT COLLATE NOCASE, tags TEXT NOT NULL, color TEXT NOT NULL, notes TEXT NOT NULL, sort_order INTEGER NOT NULL, enabled INTEGER NOT NULL, capabilities INTEGER NOT NULL, credential_ref TEXT, auth_state TEXT NOT NULL, removed INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS accounts_alias ON accounts(alias) WHERE alias IS NOT NULL AND removed=0;
            CREATE TABLE IF NOT EXISTS credentials(id TEXT PRIMARY KEY, protected BLOB NOT NULL);
            INSERT OR IGNORE INTO accounts VALUES($local,'本地邮箱','local','',NULL,'[]','#2563EB','',0,1,12,NULL,'无需登录',0);
            """;
        command.Parameters.AddWithValue("$local", SqliteMailStore.LocalAccount.ToString()); command.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<MailAccount>> List(CancellationToken cancellation = default) => Task.Run<IReadOnlyList<MailAccount>>(() =>
    {
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM accounts WHERE removed=0 ORDER BY sort_order,name,id";
        using var reader = command.ExecuteReader(); var result = new List<MailAccount>();
        while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); result.Add(Read(reader)); }
        return result;
    }, cancellation);

    public Task<MailAccount> Save(AccountEdit edit, string? newCredential = null, CancellationToken cancellation = default) => Task.Run(() => SaveCore(edit, newCredential, false, cancellation), cancellation);

    // Restore never overwrites another account. A caller must explicitly remap conflicting IDs/aliases.
    public Task<MailAccount> Restore(MailAccount account, CancellationToken cancellation = default) => Task.Run(() => SaveCore(new(account.Id, account.Name, account.Provider, account.Address, account.Alias, account.Tags, account.Color, account.Notes, account.Order, account.Enabled, account.Capabilities), null, true, cancellation), cancellation);

    internal Task<MailAccount> CreateImported(AccountEdit edit, ProtocolSettings settings, CancellationToken token) =>
        Task.Run(() => SaveCore(edit, null, false, token, settings), token);

    private MailAccount SaveCore(AccountEdit edit, string? secret, bool restoring, CancellationToken cancellation, ProtocolSettings? imported = null)
    {
        if (string.IsNullOrWhiteSpace(edit.Name)) throw new InvalidDataException("请输入账户名称。");
        var alias = string.IsNullOrWhiteSpace(edit.Alias) ? null : edit.Alias.Trim();
        if (alias is not null && !Regex.IsMatch(alias, "^[A-Za-z][A-Za-z0-9_-]{0,63}$")) throw new InvalidDataException("alias 须以字母开头，使用字母、数字、下划线或短横线，最多 64 字符。");
        if (!Regex.IsMatch(edit.Color, "^#[0-9a-fA-F]{6}$")) throw new InvalidDataException("颜色格式应为 #RRGGBB。");
        var capabilities = edit.Provider switch
        {
            "local" => AccountCapabilities.Folders | AccountCapabilities.Import,
            "imap-smtp" => edit.Capabilities & (AccountCapabilities.Send | AccountCapabilities.Receive),
            "imap" or "pop3" => AccountCapabilities.Receive,
            "smtp" => AccountCapabilities.Send,
            "outlook-com" => AccountCapabilities.Receive | AccountCapabilities.Folders,
            _ when ProviderTypes.FirstOrDefault(t => t.Id == edit.Provider) is { } type => AccountCapabilities.Receive | AccountCapabilities.Folders |
                (type.Features.HasFlag(MailboxFeatures.Send) ? AccountCapabilities.Send : 0) | (type.Features.HasFlag(MailboxFeatures.Upload) ? AccountCapabilities.Import : 0),
            _ when edit.Id != null && edit.Provider.Contains('/') => edit.Capabilities,
            _ => throw new InvalidDataException("未知账户类型或提供者插件已停用。")
        };
        if (edit.Provider != "local" && (!MimeKit.MailboxAddress.TryParse(edit.Address, out var address) || address.Address != edit.Address.Trim())) throw new InvalidDataException("请输入有效邮箱地址。");
        if (capabilities == AccountCapabilities.None) throw new InvalidDataException("在线账户至少选择接收或发送能力。");
        if (edit.Provider is "imap" or "imap-smtp" && capabilities.HasFlag(AccountCapabilities.Receive)) capabilities |= AccountCapabilities.Folders;
        var tags = (edit.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var db = _store.Open(); using var transaction = db.BeginTransaction();
        if (imported != null)
        {
            using var match = db.CreateCommand();
            match.Transaction = transaction;
            match.CommandText = "SELECT EXISTS(SELECT 1 FROM accounts WHERE removed=0 AND provider=$provider AND address=$address COLLATE NOCASE)";
            match.Parameters.AddWithValue("$provider", edit.Provider);
            match.Parameters.AddWithValue("$address", edit.Address.Trim());
            if (Convert.ToBoolean(match.ExecuteScalar())) throw new InvalidDataException("已存在相同类型和地址的账户。请刷新匹配结果并选择已有账户。");
        }
        MailAccount? existing = null;
        if (edit.Id is { } editId)
        {
            using var find = db.CreateCommand(); find.Transaction = transaction; find.CommandText = "SELECT * FROM accounts WHERE id=$id"; find.Parameters.AddWithValue("$id", editId.ToString());
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                if (restoring) throw new InvalidDataException("恢复冲突：账户 ID 已存在，请显式重新映射。");
                if (reader.GetBoolean(13)) throw new InvalidDataException("账户已移除，请刷新列表。");
                existing = Read(reader);
            }
            else if (!restoring) throw new InvalidDataException("账户不存在，请重新选择。");
        }
        if (existing is not null && existing.Provider != edit.Provider) throw new InvalidDataException("账户类型创建后不可更改；请新建对应类型账户。");
        if (existing != null && existing.Provider.Contains('/') && !ProviderTypes.Any(p => p.Id == existing.Provider))
            capabilities = existing.Capabilities;
        if (existing?.Provider == "outlook-com" && existing.Address.Equals(edit.Address.Trim(), StringComparison.OrdinalIgnoreCase))
            capabilities |= existing.Capabilities & AccountCapabilities.Send;
        var id = edit.Id ?? Guid.NewGuid();
        var reference = existing?.CredentialReference;
        var authentication = edit.Provider == "local" ? "无需登录" : existing?.AuthenticationState ?? "需要授权 · 请配置服务器";
        if (!string.IsNullOrEmpty(secret) && edit.Provider != "local")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭据保护仅支持 Windows。");
            var plain = Encoding.UTF8.GetBytes(secret);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            reference ??= Guid.NewGuid().ToString();
            using var credential = db.CreateCommand(); credential.Transaction = transaction; credential.CommandText = "INSERT INTO credentials VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET protected=excluded.protected";
            credential.Parameters.AddWithValue("$id", reference); credential.Parameters.AddWithValue("$value", encrypted); credential.ExecuteNonQuery();
            authentication = "凭据已更新 · 等待连接验证";
        }
        var account = new MailAccount(id, edit.Name.Trim(), edit.Provider, edit.Address.Trim(), alias, tags, edit.Color, edit.Notes, edit.Order, edit.Enabled, capabilities, reference, authentication);
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO accounts VALUES($id,$name,$provider,$address,$alias,$tags,$color,$notes,$order,$enabled,$capabilities,$credential,$auth,0)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,address=excluded.address,alias=excluded.alias,tags=excluded.tags,color=excluded.color,notes=excluded.notes,sort_order=excluded.sort_order,enabled=excluded.enabled,capabilities=excluded.capabilities,credential_ref=excluded.credential_ref,auth_state=excluded.auth_state;
            """;
        command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$name", account.Name); command.Parameters.AddWithValue("$provider", account.Provider);
        command.Parameters.AddWithValue("$address", account.Address); command.Parameters.AddWithValue("$alias", (object?)alias ?? DBNull.Value); command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(tags));
        command.Parameters.AddWithValue("$color", account.Color); command.Parameters.AddWithValue("$notes", account.Notes); command.Parameters.AddWithValue("$order", account.Order);
        command.Parameters.AddWithValue("$enabled", account.Enabled); command.Parameters.AddWithValue("$capabilities", (int)capabilities); command.Parameters.AddWithValue("$credential", (object?)reference ?? DBNull.Value); command.Parameters.AddWithValue("$auth", authentication);
        cancellation.ThrowIfCancellationRequested();
        try { command.ExecuteNonQuery(); } catch (SqliteException e) when (e.SqliteExtendedErrorCode == 2067) { throw new InvalidDataException("alias 已被另一账户使用，请使用不同 alias。", e); }
        if (existing is null && edit.Provider == "local")
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO folders VALUES($id,$account,'收件箱',$path,'inbox',NULL)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); command.Parameters.AddWithValue("$account", id.ToString()); command.Parameters.AddWithValue("$path", account.Name + " / 收件箱"); command.ExecuteNonQuery();
        }
        if (imported != null)
        {
            command.CommandText = "INSERT INTO protocol_settings(account_id,settings) VALUES($id,$settings)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", account.Id.ToString());
            command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(imported));
            command.ExecuteNonQuery();
        }
        cancellation.ThrowIfCancellationRequested(); transaction.Commit(); return account;
    }

    public Task Remove(Guid id, CancellationToken cancellation = default) => Task.Run(() =>
    {
        using var db = _store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM credentials WHERE id IN (SELECT credential_ref FROM accounts WHERE id=$id); UPDATE accounts SET removed=1,enabled=0,credential_ref=NULL,auth_state='已移除' WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString()); cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); transaction.Commit();
    }, cancellation);

    public Task<string?> ReadCredential(Guid accountId, CancellationToken cancellation = default) => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var db = _store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT protected FROM credentials JOIN accounts ON credentials.id=accounts.credential_ref WHERE accounts.id=$id AND accounts.enabled=1 AND accounts.removed=0";
        command.Parameters.AddWithValue("$id", accountId.ToString()); cancellation.ThrowIfCancellationRequested();
        if (command.ExecuteScalar() is not byte[] encrypted) return null;
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
    }, cancellation);

    public Task AuthenticationFailed(Guid id, CancellationToken cancellation = default) => Task.Run(() =>
    {
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE accounts SET auth_state='授权失败，请更新凭据后重试' WHERE id=$id AND removed=0";
        command.Parameters.AddWithValue("$id", id.ToString()); cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
    }, cancellation);

    private static MailAccount Read(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), JsonSerializer.Deserialize<string[]>(r.GetString(5))!, r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetBoolean(9), (AccountCapabilities)r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetString(12));
}
