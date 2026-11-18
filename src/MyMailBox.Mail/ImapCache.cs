using MailKit;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace MyMailBox.Mail;

internal sealed partial class ImapCache(SqliteMailStore store, Guid account, MailEndpoint endpoint)
{
    private readonly string _source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{endpoint.Host.ToLowerInvariant()}:{endpoint.Port}/{endpoint.User}")));

    internal sealed record Folder(Guid Id, uint Validity, uint Watermark, int Coverage);
    internal static void Initialize(SqliteMailStore store)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS imap_folders(
                id TEXT PRIMARY KEY REFERENCES folders(id) ON DELETE CASCADE, account TEXT NOT NULL, source TEXT NOT NULL,
                path TEXT NOT NULL, validity INTEGER NOT NULL, watermark INTEGER NOT NULL, coverage INTEGER NOT NULL,
                UNIQUE(account,source,path));
            CREATE TABLE IF NOT EXISTS imap_edits(id TEXT PRIMARY KEY,account TEXT NOT NULL,folder TEXT NOT NULL,uid INTEGER NOT NULL,
                path TEXT NOT NULL,subject TEXT NOT NULL,mark INTEGER NOT NULL,state TEXT NOT NULL,detail TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS imap_edits_folder ON imap_edits(folder,state);
            CREATE TRIGGER IF NOT EXISTS imap_detached BEFORE DELETE ON imap_folders BEGIN
                UPDATE imap_edits SET state='conflict',detail='目录已改名、移除或重新编号。旧标识已停用，请重新选择邮件。'
                WHERE folder=OLD.id AND state='pending';
            END;
            CREATE TABLE IF NOT EXISTS imap_summaries(
                folder TEXT NOT NULL REFERENCES imap_folders(id) ON DELETE CASCADE, uid INTEGER NOT NULL,
                subject TEXT NOT NULL, sender TEXT NOT NULL, size INTEGER NOT NULL,
                message TEXT REFERENCES messages(id) ON DELETE SET NULL, missing INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(folder,uid));
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT count(*) FROM pragma_table_info('imap_summaries') WHERE name='date_ms'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
        {
            command.CommandText = "ALTER TABLE imap_summaries ADD COLUMN date_ms INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT count(*) FROM pragma_table_info('imap_summaries') WHERE name='flags'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
        {
            command.CommandText = "ALTER TABLE imap_summaries ADD COLUMN flags INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT count(*) FROM pragma_table_info('imap_summaries') WHERE name='envelope'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
        {
            command.CommandText = "ALTER TABLE imap_summaries ADD COLUMN envelope TEXT";
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private SqliteCommand Lookup(SqliteConnection db, string path, SqliteTransaction? transaction = null)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$source", _source);
        command.Parameters.AddWithValue("$path", path);
        command.CommandText = "SELECT id,validity,watermark,coverage FROM imap_folders WHERE account=$account AND source=$source AND path=$path";
        return command;
    }

    internal IReadOnlyList<string> Paths()
    {
        using var db = store.Open();
        using var command = Lookup(db, "");
        command.CommandText = "SELECT path FROM imap_folders WHERE account=$account AND source=$source ORDER BY path";
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    internal (string Path, uint Validity) Remote(Guid folder)
    {
        using var db = store.Open();
        using var c = Lookup(db, "");
        c.CommandText = "SELECT path,validity FROM imap_folders WHERE account=$account AND source=$source AND id=$id";
        c.Parameters.AddWithValue("$id",folder.ToString());
        using var r = c.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException("目录引用已失效，请重新选择目录。");
        return (r.GetString(0),(uint)r.GetInt64(1));
    }

    internal void Detach(string path)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var c = Lookup(db, path, tx);
        c.CommandText = """
            UPDATE folders SET role='imap-detached' WHERE id IN
                (SELECT id FROM imap_folders WHERE account=$account AND source=$source AND path=$path);
            DELETE FROM imap_folders WHERE account=$account AND source=$source AND path=$path;
            """;
        c.ExecuteNonQuery(); tx.Commit();
    }

    internal void Renamed(string old, string renamed, char separator, string accountName)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var c = Lookup(db, old, tx);
        c.Parameters.AddWithValue("$new",renamed); c.Parameters.AddWithValue("$prefix",old + separator);
        c.Parameters.AddWithValue("$name",accountName);
        c.CommandText = """
            UPDATE imap_edits SET path=$new || substr(path,length($path)+1)
            WHERE folder IN (SELECT id FROM imap_folders WHERE account=$account AND source=$source)
                AND (path=$path OR substr(path,1,length($prefix))=$prefix);
            UPDATE imap_folders SET path=$new || substr(path,length($path)+1)
            WHERE account=$account AND source=$source AND (path=$path OR substr(path,1,length($prefix))=$prefix);
            UPDATE folders SET name=(SELECT path FROM imap_folders WHERE id=folders.id),
                path=$name || ' / ' || (SELECT path FROM imap_folders WHERE id=folders.id) || ' (IMAP)'
            WHERE id IN (SELECT id FROM imap_folders WHERE account=$account AND source=$source);
            """;
        c.ExecuteNonQuery(); tx.Commit();
    }

    internal Folder Start(string path, uint validity, string accountName)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var command = Lookup(db, path, tx);
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read() && reader.GetInt64(1) == validity)
                return new(Guid.Parse(reader.GetString(0)), validity, (uint)reader.GetInt64(2), reader.GetInt32(3));
        }
        // Expire remote references only. Downloaded MIME in the previous local folder remains readable.
        var id = Guid.NewGuid();
        command.CommandText = """
            UPDATE folders SET role='imap-detached' WHERE id IN
                (SELECT id FROM imap_folders WHERE account=$account AND source=$source AND path=$path);
            DELETE FROM imap_folders WHERE account=$account AND source=$source AND path=$path;
            INSERT INTO folders VALUES($id,$account,$path,$display,'imap-cache',NULL);
            INSERT INTO imap_folders VALUES($id,$account,$source,$path,$validity,0,0);
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$display", $"{accountName} / {path} (IMAP)");
        command.Parameters.AddWithValue("$validity", (long)validity);
        command.ExecuteNonQuery();
        tx.Commit();
        return new(id, validity, 0, 0);
    }

    internal HashSet<UniqueId> MissingSummaries(Folder folder, IReadOnlyList<UniqueId> candidates)
    {
        var missing = candidates.ToHashSet();
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT uid FROM imap_summaries WHERE folder=$folder AND uid BETWEEN $low AND $high";
        command.Parameters.AddWithValue("$folder", folder.Id.ToString());
        command.Parameters.AddWithValue("$low", (long)candidates[^1].Id);
        command.Parameters.AddWithValue("$high", (long)candidates[0].Id);
        using var reader = command.ExecuteReader();
        while (reader.Read()) missing.Remove(new UniqueId((uint)reader.GetInt64(0)));
        return missing;
    }

    internal int Add(Folder folder, IEnumerable<IMessageSummary> batch, CancellationToken token)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO imap_summaries(folder,uid,subject,sender,size,date_ms,flags,envelope) VALUES($folder,$uid,$subject,$sender,$size,$date,$flags,$env)
            ON CONFLICT(folder,uid) DO NOTHING
            """;
        command.Parameters.AddWithValue("$folder", folder.Id.ToString());
        var uid = command.Parameters.Add("$uid", SqliteType.Integer);
        var subject = command.Parameters.Add("$subject", SqliteType.Text);
        var sender = command.Parameters.Add("$sender", SqliteType.Text);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var date = command.Parameters.Add("$date", SqliteType.Integer);
        var flags = command.Parameters.Add("$flags",SqliteType.Integer);
        var envelope = command.Parameters.Add("$env",SqliteType.Text);
        int added = 0;
        foreach (var item in batch)
        {
            token.ThrowIfCancellationRequested();
            uid.Value = (long)item.UniqueId.Id;
            subject.Value = item.Envelope?.Subject ?? "（无主题）";
            sender.Value = item.Envelope?.From.ToString() ?? "";
            size.Value = (long)(item.Size ?? 0);
            date.Value = item.Envelope?.Date?.ToUnixTimeMilliseconds() ?? 0;
            envelope.Value = System.Text.Json.JsonSerializer.Serialize(new {
                To = item.Envelope?.To.ToString(), Cc = item.Envelope?.Cc.ToString(), Bcc = item.Envelope?.Bcc.ToString(),
                Reply = item.Envelope?.ReplyTo.ToString(), Sender = item.Envelope?.Sender.ToString(), Thread = item.Envelope?.InReplyTo ?? item.Envelope?.MessageId,
                Received = item.InternalDate?.ToUnixTimeMilliseconds()
            });
            flags.Value = (int)(item.Flags ?? MessageFlags.None);
            added += command.ExecuteNonQuery();
        }
        tx.Commit();
        return added;
    }

    internal void Received(Folder folder, IEnumerable<UniqueId> items)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction();
        foreach (var uid in items)
        {
            string key = uid.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RuleInbox.Arrived(tx,folder.Id,key,folder.Id + "/" + key,folder.Coverage==0 || uid.Id<=folder.Watermark);
        }
        tx.Commit();
    }

    internal void Finish(Folder folder, uint watermark, int coverage)
    {
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE imap_folders SET watermark=$watermark, coverage=$coverage WHERE id=$id";
        command.Parameters.AddWithValue("$id", folder.Id.ToString());
        command.Parameters.AddWithValue("$watermark", (long)watermark);
        command.Parameters.AddWithValue("$coverage", coverage);
        command.ExecuteNonQuery();
    }

    internal ImapCachePage Page(string path, int offset, int limit)
    {
        using var db = store.Open();
        using var command = Lookup(db, path);
        command.CommandText = """
            SELECT f.id,f.validity,s.uid,s.subject,s.sender,s.size,s.message,s.missing,s.flags
            FROM imap_folders f JOIN imap_summaries s ON s.folder=f.id
            WHERE f.account=$account AND f.source=$source AND f.path=$path ORDER BY s.uid DESC LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var items = new List<ImapCachedMail>();
        while (reader.Read())
            items.Add(new(Guid.Parse(reader.GetString(0)), (uint)reader.GetInt64(1), (uint)reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), reader.GetBoolean(7))
                { IsRead = ((MessageFlags)reader.GetInt32(8)).HasFlag(MessageFlags.Seen), IsStarred = ((MessageFlags)reader.GetInt32(8)).HasFlag(MessageFlags.Flagged) });
        return new(items.Take(limit).ToArray(), items.Count > limit);
    }

    internal IReadOnlyList<InboxMessage> Inbox(MailAccount identity, InboxPosition? before, int limit)
    {
        using var db = store.Open();
        using var command = Lookup(db, "INBOX");
        command.CommandText = """
            SELECT s.date_ms,s.folder || '/' || printf('%010d',s.uid) AS item_key,s.subject,s.sender,s.message IS NOT NULL
            FROM imap_summaries s JOIN imap_folders f ON f.id=s.folder
            WHERE f.account=$account AND f.source=$source AND f.path=$path COLLATE NOCASE AND s.missing=0
            AND (s.date_ms,item_key)<($time,$key) ORDER BY s.date_ms DESC,item_key DESC LIMIT $limit
            """;
        return ProtocolReceiveProvider.ReadInbox(command, identity, before, limit);
    }

    internal ImapCachedMail FindInboxMessage(string key)
    {
        using var db = store.Open();
        using var command = Lookup(db, "INBOX");
        command.CommandText = """
            SELECT f.id,f.validity,s.uid,s.subject,s.sender,s.size,s.message,s.missing,s.flags
            FROM imap_folders f JOIN imap_summaries s ON s.folder=f.id
            WHERE f.account=$account AND f.source=$source AND f.path=$path COLLATE NOCASE
                AND s.folder || '/' || printf('%010d',s.uid)=$key
            """;
        command.Parameters.AddWithValue("$key", key);
        using var r = command.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException("此邮件已不在当前收件箱，请刷新。");
        return new(Guid.Parse(r.GetString(0)), (uint)r.GetInt64(1), (uint)r.GetInt64(2), r.GetString(3), r.GetString(4),
            r.GetInt64(5), r.IsDBNull(6) ? null : Guid.Parse(r.GetString(6)), r.GetBoolean(7));
    }

    private SqliteCommand FindMessage(SqliteConnection db, string path, ImapCachedMail item, SqliteTransaction? transaction = null)
    {
        var command = Lookup(db, path, transaction);
        command.Parameters.AddWithValue("$folder", item.FolderId.ToString());
        command.Parameters.AddWithValue("$validity", (long)item.Validity);
        command.Parameters.AddWithValue("$uid", (long)item.Uid);
        command.CommandText = """
            SELECT s.message,s.missing FROM imap_summaries s JOIN imap_folders f ON s.folder=f.id
            WHERE f.id=$folder AND f.account=$account AND f.source=$source AND f.path=$path AND f.validity=$validity AND s.uid=$uid
            """;
        return command;
    }

    private static Guid? ReadMapping(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("目录标识已变更，请重新同步并选择邮件。");
        if (!reader.IsDBNull(0)) return Guid.Parse(reader.GetString(0));
        if (reader.GetBoolean(1)) throw new InvalidDataException("服务器上已不存在这封邮件。");
        return null;
    }

    internal Guid? LocalId(string path, ImapCachedMail item)
    {
        using var db = store.Open();
        using var command = FindMessage(db, path, item);
        return ReadMapping(command);
    }

    internal void MarkMissing(ImapCachedMail item)
    {
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE imap_summaries SET missing=1 WHERE folder=$folder AND uid=$uid";
        command.Parameters.AddWithValue("$folder", item.FolderId.ToString());
        command.Parameters.AddWithValue("$uid", (long)item.Uid);
        command.ExecuteNonQuery();
    }

    internal Guid Save(string path, ImapCachedMail item, byte[] raw, ParsedMail parsed, CancellationToken token)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var command = FindMessage(db, path, item, tx);
        if (ReadMapping(command) is Guid existing) return existing;
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        var id = SqliteMailStore.InsertMessage(insert, item.FolderId, raw, parsed);
        command.CommandText = """
            UPDATE imap_summaries SET message=$message WHERE folder=$folder AND uid=$uid;
            UPDATE messages SET is_read=(SELECT (flags & $seen)<>0 FROM imap_summaries WHERE folder=$folder AND uid=$uid) WHERE id=$message;
            """;
        command.CommandText += " UPDATE rule_run SET state='pending',detail='正文已可用，等待判断' WHERE folder=$folder AND item=CAST($uid AS TEXT) AND state='waiting';";
        command.Parameters.AddWithValue("$seen",(int)MessageFlags.Seen);
        command.Parameters.AddWithValue("$message", id.ToString());
        command.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return id;
    }
}
