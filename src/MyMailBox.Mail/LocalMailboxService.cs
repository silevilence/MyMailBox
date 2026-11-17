using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class LocalMailboxService(SqliteMailStore store, AccountService accounts, IMimeReader parser) : ILocalMailboxService
{
    public string StorageDescription => $"目录式工作空间 v1：{Path.GetDirectoryName(Path.GetDirectoryName(store.DatabasePath))}。邮件原文、目录和元数据保存在 mail/mail.db（含 WAL 事务）。使用“存储与恢复”校验，“工作空间备份”备份，或“邮件归档包”搬运单个邮箱。运行时单文件容器不属于当前格式。";
    public Task<IReadOnlyList<LocalMailboxInfo>> List(CancellationToken cancellation = default) => Task.Run<IReadOnlyList<LocalMailboxInfo>>(() =>
    {
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = """
            SELECT a.id,a.name,a.alias,a.tags,a.enabled,a.removed,
            (SELECT COUNT(*) FROM folders f WHERE f.account_id=a.id),
            (SELECT COUNT(*) FROM messages m JOIN folders f ON f.id=m.folder_id WHERE f.account_id=a.id)
            FROM accounts a WHERE a.provider='local' ORDER BY a.sort_order,a.name,a.id
            """;
        using var reader = command.ExecuteReader(); var result = new List<LocalMailboxInfo>();
        while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), JsonSerializer.Deserialize<string[]>(reader.GetString(3))!, reader.GetBoolean(4) && !reader.GetBoolean(5), reader.GetBoolean(5), reader.GetInt32(6), reader.GetInt32(7))); }
        return result;
    }, cancellation);
    public Task<MailAccount> Create(AccountEdit edit, CancellationToken cancellation = default) => accounts.Save(edit with { Id = null, Provider = "local", Address = "", Enabled = true }, cancellation: cancellation);
    public Task SetOpen(Guid id, bool open, string? alias, CancellationToken cancellation = default) => Task.Run(() =>
    {
        alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (alias is not null && !System.Text.RegularExpressions.Regex.IsMatch(alias, "^[A-Za-z][A-Za-z0-9_-]{0,63}$")) throw new InvalidDataException("alias 须以字母开头，最多 64 个字母、数字、下划线或短横线。");
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = open
            ? "UPDATE accounts SET enabled=1,removed=0,alias=$alias,auth_state='无需登录' WHERE id=$id AND provider='local'"
            : "UPDATE accounts SET enabled=0 WHERE id=$id AND provider='local'";
        command.Parameters.AddWithValue("$id", id.ToString()); if (open) command.Parameters.AddWithValue("$alias", (object?)alias ?? DBNull.Value);
        cancellation.ThrowIfCancellationRequested();
        try { if (command.ExecuteNonQuery() == 0) throw new InvalidDataException("本地邮箱不存在。"); }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == 2067) { throw new InvalidDataException("alias 已被使用。请更改下方 alias 后重新打开；归档仍保留。", e); }
    }, cancellation);
    public async Task<IReadOnlyList<MailFolder>> Folders(Guid id, CancellationToken cancellation = default) => (await store.Folders(cancellation)).Where(f => f.AccountId == id).ToArray();
    public Task<MailFolder> CreateFolder(Guid account, Guid? parent, string name, CancellationToken cancellation = default) => Task.Run(() =>
    {
        name = name.Trim(); if (name.Length is 0 or > 120 || name.Any(c => c is '/' or '\\' || char.IsControl(c))) throw new InvalidDataException("目录名称需为 1–120 字，不能包含斜线或控制字符。");
        using var db = store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT name FROM accounts WHERE id=$id AND provider='local' AND enabled=1 AND removed=0"; command.Parameters.AddWithValue("$id", account.ToString());
        var accountName = command.ExecuteScalar() as string ?? throw new InvalidDataException("请先打开本地邮箱。");
        var prefix = accountName;
        if (parent is { } parentId)
        {
            command.Parameters.Clear(); command.CommandText = "SELECT path FROM folders WHERE id=$id AND account_id=$account";
            command.Parameters.AddWithValue("$id", parentId.ToString()); command.Parameters.AddWithValue("$account", account.ToString());
            prefix = command.ExecuteScalar() as string ?? throw new InvalidDataException("父目录不存在或属于另一邮箱。");
        }
        command.Parameters.Clear(); command.CommandText = "SELECT COUNT(*) FROM folders WHERE account_id=$account AND parent_id IS $parent AND name=$name COLLATE NOCASE";
        command.Parameters.AddWithValue("$account", account.ToString()); command.Parameters.AddWithValue("$parent", parent?.ToString() is { } p ? p : DBNull.Value); command.Parameters.AddWithValue("$name", name);
        if (Convert.ToInt32(command.ExecuteScalar()) > 0) throw new InvalidDataException("同一层已有同名目录。");
        var folder = new MailFolder(Guid.NewGuid(), account, name, prefix + " / " + name, "archive", parent);
        command.CommandText = "INSERT INTO folders VALUES($id,$account,$name,$path,'archive',$parent)"; command.Parameters.AddWithValue("$id", folder.Id.ToString()); command.Parameters.AddWithValue("$path", folder.Path);
        cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); transaction.Commit(); return folder;
    }, cancellation);
    public async Task<Guid> Archive(Guid folder, byte[] raw, CancellationToken cancellation = default)
    {
        // Import shares the same transaction and complete-MIME checks as regular local mail.
        return await new AvailableMailStore(store).Import(folder, raw, await parser.Parse(raw, cancellation), cancellation);
    }
}
