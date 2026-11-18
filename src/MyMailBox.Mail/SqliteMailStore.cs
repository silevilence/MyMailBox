using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Security.Cryptography;

namespace MyMailBox.Mail;

public sealed class SqliteMailStore : IMailStore
{
    public static readonly Guid LocalAccount = Guid.Parse("ae099736-b044-4e6a-94f6-c9c638b92712");
    public static readonly Guid Inbox = Guid.Parse("c8438889-e62a-4b4e-a731-3af064d0fc96");
    public string DatabasePath { get; }

    public SqliteMailStore(string directory)
    {
        Directory.CreateDirectory(directory);
        DatabasePath = Path.GetFullPath(Path.Combine(directory, "mail.db"));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 1) throw new InvalidDataException($"工作空间版本 {version} 高于当前支持的版本 1。请使用新版应用；原数据未修改。");
        command.CommandText = "PRAGMA journal_mode=WAL"; command.ExecuteNonQuery();
        using var transaction = db.BeginTransaction(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS folders(id TEXT PRIMARY KEY, account_id TEXT NOT NULL, name TEXT NOT NULL, path TEXT NOT NULL, role TEXT NOT NULL, parent_id TEXT);
            CREATE TABLE IF NOT EXISTS messages(id TEXT PRIMARY KEY, folder_id TEXT NOT NULL REFERENCES folders(id), subject TEXT NOT NULL, sender TEXT NOT NULL, recipient TEXT NOT NULL, date TEXT NOT NULL, size INTEGER NOT NULL, is_read INTEGER NOT NULL DEFAULT 0, raw BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS messages_folder ON messages(folder_id, date DESC, id);
            """;
        command.ExecuteNonQuery();
        if (version == 0)
        {
            command.CommandText = "ALTER TABLE messages ADD COLUMN sha256 TEXT; CREATE TABLE IF NOT EXISTS disposable_cache(id TEXT PRIMARY KEY, content BLOB NOT NULL); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
            // Existing v0 MIME remains authoritative; fill its digest without reparsing it.
            command.CommandText = "SELECT id,raw FROM messages";
            var digests = new List<(string Id, string Hash)>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) digests.Add((reader.GetString(0), Convert.ToHexString(SHA256.HashData((byte[])reader[1]))));
            foreach (var item in digests)
            {
                command.Parameters.Clear(); command.CommandText = "UPDATE messages SET sha256=$hash WHERE id=$id";
                command.Parameters.AddWithValue("$hash", item.Hash); command.Parameters.AddWithValue("$id", item.Id); command.ExecuteNonQuery();
            }
        }
        command.Parameters.Clear();
        command.CommandText = "INSERT OR IGNORE INTO folders VALUES($id,$account,'收件箱','本地邮箱 / 收件箱','inbox',NULL)";
        command.Parameters.AddWithValue("$id", Inbox.ToString()); command.Parameters.AddWithValue("$account", LocalAccount.ToString());
        command.ExecuteNonQuery();
        RuleInbox.Initialize(command);
        transaction.Commit();
    }

    public SqliteConnection Open() => Open(false);
    internal SqliteConnection Open(bool readOnly)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode=readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false }.ToString());
        db.Open(); return db;
    }

    public Task<IReadOnlyList<MailFolder>> Folders(CancellationToken cancellation = default) => Task.Run<IReadOnlyList<MailFolder>>(() =>
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id,account_id,name,path,role,parent_id FROM folders ORDER BY path";
        using var reader = command.ExecuteReader(); var folders = new List<MailFolder>();
        while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); folders.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)))); }
        return folders;
    }, cancellation);

    public Task<MailPageResult> List(Guid folderId, int offset, int limit, CancellationToken cancellation = default) => Task.Run(() =>
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id,subject,sender,recipient,date,size,is_read FROM messages WHERE folder_id=$folder ORDER BY date DESC,id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$folder", folderId.ToString()); command.Parameters.AddWithValue("$limit", limit + 1); command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader(); var items = new List<MailSummary>();
        while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); items.Add(new(Guid.Parse(reader.GetString(0)), folderId, reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt64(5), reader.GetBoolean(6))); }
        return new MailPageResult(items.Take(limit).ToArray(), offset, items.Count > limit);
    }, cancellation);

    public Task<Guid> Import(Guid folderId, byte[] raw, ParsedMail metadata, CancellationToken cancellation = default) => Task.Run(() =>
    {
        if (raw.Length is 0 or > MimeReader.MaxMessageBytes) throw new InvalidDataException("邮件为空或超过 25 MiB 存储限制。");
        using var db = Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        var id = InsertMessage(command, folderId, raw, metadata);
        RuleInbox.Arrived(transaction,folderId,id.ToString(),id.ToString(),true);
        cancellation.ThrowIfCancellationRequested(); transaction.Commit(); return id;
    }, cancellation);

    internal static Guid InsertMessage(SqliteCommand command, Guid folderId, byte[] raw, ParsedMail metadata)
    {
        var id = Guid.NewGuid();
        command.CommandText = "INSERT INTO messages(id,folder_id,subject,sender,recipient,date,size,raw,sha256) VALUES($id,$folder,$subject,$from,$to,$date,$size,$raw,$hash)";
        command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$folder", folderId.ToString());
        command.Parameters.AddWithValue("$subject", metadata.Subject); command.Parameters.AddWithValue("$from", metadata.From); command.Parameters.AddWithValue("$to", metadata.To);
        command.Parameters.AddWithValue("$date", metadata.Date.ToUniversalTime().ToString("O")); command.Parameters.AddWithValue("$size", raw.Length); command.Parameters.AddWithValue("$raw", raw);
        command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(raw)));
        command.ExecuteNonQuery(); return id;
    }

    public Task<byte[]> Read(Guid messageId, CancellationToken cancellation = default) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested(); using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT raw FROM messages WHERE id=$id"; command.Parameters.AddWithValue("$id", messageId.ToString());
        return command.ExecuteScalar() as byte[] ?? throw new FileNotFoundException("邮件已不存在，请刷新列表。");
    }, cancellation);
}
