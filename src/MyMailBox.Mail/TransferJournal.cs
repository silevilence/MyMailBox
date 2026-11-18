using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyMailBox.Mail;

internal sealed record TransferContent(byte[] Raw, bool Read, bool Starred, string[] Labels);
internal sealed class TransferJournal(SqliteMailStore store)
{
    internal static void Initialize(SqliteMailStore store)
    {
        using var db = store.Open(); using var c = db.CreateCommand();
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS transfers(
                id TEXT PRIMARY KEY,source_account TEXT NOT NULL,source_folder TEXT NOT NULL,source_key TEXT NOT NULL,
                dest TEXT NOT NULL,move INTEGER NOT NULL,source_path TEXT NOT NULL,dest_path TEXT NOT NULL,
                phase TEXT NOT NULL,detail TEXT NOT NULL,target TEXT,hash TEXT,created INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS transfers_account ON transfers(source_account,created);
            CREATE INDEX IF NOT EXISTS transfers_target ON transfers(target);
            CREATE TABLE IF NOT EXISTS transfer_events(sequence INTEGER PRIMARY KEY,operation TEXT NOT NULL REFERENCES transfers(id),
                time INTEGER NOT NULL,phase TEXT NOT NULL,detail TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS transfer_events_operation ON transfer_events(operation,sequence);
            CREATE TRIGGER IF NOT EXISTS transfer_planned AFTER INSERT ON transfers BEGIN
                INSERT INTO transfer_events(operation,time,phase,detail) VALUES(NEW.id,unixepoch(),NEW.phase,NEW.detail);
            END;
            CREATE TRIGGER IF NOT EXISTS transfer_progress AFTER UPDATE ON transfers
              WHEN OLD.phase<>NEW.phase OR OLD.detail<>NEW.detail BEGIN
                INSERT INTO transfer_events(operation,time,phase,detail) VALUES(NEW.id,unixepoch(),NEW.phase,NEW.detail);
            END;
            CREATE TRIGGER IF NOT EXISTS protect_transfer_target BEFORE DELETE ON messages
              WHEN EXISTS(SELECT 1 FROM transfers WHERE target=OLD.id AND phase='cleaning')
              BEGIN SELECT RAISE(ABORT,'transfer cleanup in progress'); END;
            CREATE TRIGGER IF NOT EXISTS protect_transfer_body BEFORE UPDATE OF raw,folder_id ON messages
              WHEN EXISTS(SELECT 1 FROM transfers WHERE target=OLD.id AND phase='cleaning')
              BEGIN SELECT RAISE(ABORT,'transfer cleanup in progress'); END;
            """;
        c.ExecuteNonQuery();
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        var c = db.CreateCommand(); c.CommandText = sql;
        foreach (var (key, value) in args) c.Parameters.AddWithValue("$" + key, value);
        return c;
    }

    internal IReadOnlyList<TransferEvent> History(Guid id)
    {
        using var db = store.Open();
        using var c = Command(db, "SELECT time,phase,detail FROM transfer_events WHERE operation=$id ORDER BY sequence", ("id",id.ToString()));
        using var r = c.ExecuteReader();
        var events = new List<TransferEvent>();
        while (r.Read()) events.Add(new(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),r.GetString(1),r.GetString(2)));
        return events;
    }

    internal void Plan(TransferRequest r, string source, string dest)
    {
        using var db = store.Open(); using var c = Command(db, """
            INSERT INTO transfers VALUES($id,$account,$source,$key,$dest,$move,$sp,$dp,'planned','等待读取原文',NULL,NULL,unixepoch())
            ON CONFLICT(id) DO NOTHING
            """, ("id", r.Id.ToString()), ("account", r.SourceAccount.ToString()), ("source", r.SourceFolder.ToString()), ("key", r.Key),
            ("dest", r.Destination.ToString()), ("move", r.Move), ("sp", source), ("dp", dest));
        c.ExecuteNonQuery();
    }

    private static MailTransfer Read(SqliteDataReader r) => new(
        new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), Guid.Parse(r.GetString(4)), r.GetBoolean(5)),
        r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : Guid.Parse(r.GetString(10)),
        r.IsDBNull(11) ? null : r.GetString(11), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(12)));

    internal MailTransfer Get(Guid id)
    {
        using var db = store.Open(); using var c = Command(db, "SELECT * FROM transfers WHERE id=$id", ("id", id.ToString()));
        using var r = c.ExecuteReader(); return r.Read() ? Read(r) : throw new InvalidDataException("找不到操作记录。");
    }

    internal IReadOnlyList<MailTransfer> Recent(Guid account, int offset, int limit)
    {
        using var db = store.Open(); using var c = Command(db, "SELECT * FROM transfers WHERE source_account=$account ORDER BY created DESC,rowid DESC LIMIT $limit OFFSET $offset",
            ("account", account.ToString()), ("limit", limit), ("offset", offset));
        using var r = c.ExecuteReader(); var rows = new List<MailTransfer>(); while (r.Read()) rows.Add(Read(r)); return rows;
    }

    internal void SaveTarget(MailTransfer step, TransferContent source, ParsedMail mail, CancellationToken token)
    {
        var request = step.Request;
        using var db = store.Open(); using var tx = db.BeginTransaction();
        using var c = Command(db, """
            SELECT count(*) FROM folders f JOIN accounts a ON a.id=f.account_id
              WHERE f.id=$dest AND a.provider='local' AND a.enabled AND NOT a.removed AND a.id<>$account
            """, ("dest", request.Destination.ToString()), ("account", request.SourceAccount.ToString()));
        c.Transaction = tx;
        if (Convert.ToInt32(c.ExecuteScalar()) != 1) throw new InvalidDataException("目标本地邮箱已关闭或目录已移除。");
        var hash = Convert.ToHexString(SHA256.HashData(source.Raw));
        using var insert = db.CreateCommand(); insert.Transaction = tx;
        var target = SqliteMailStore.InsertMessage(insert, request.Destination, source.Raw, mail);
        c.Parameters.AddWithValue("$target", target.ToString()); c.Parameters.AddWithValue("$read", source.Read);
        c.Parameters.AddWithValue("$star", source.Starred); c.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(source.Labels));
        c.Parameters.AddWithValue("$hash", hash); c.Parameters.AddWithValue("$op", request.Id.ToString());
        c.CommandText = """
            UPDATE messages SET is_read=$read WHERE id=$target;
            INSERT INTO mail_marks VALUES($dest,$target,$star,$labels);
            UPDATE transfers SET target=$target,hash=$hash,phase='stored',detail='目标原文已写入，等待重新读取并校验。'
            WHERE id=$op AND phase='planned';
            """;
        c.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit();
    }

    internal void VerifyTarget(MailTransfer step)
    {
        using var db = store.Open();
        using var c = Command(db, """
            SELECT m.raw,m.size,m.sha256 FROM messages m JOIN folders f ON f.id=m.folder_id JOIN accounts a ON a.id=f.account_id
            WHERE m.id=$target AND f.id=$dest AND a.enabled AND NOT a.removed
            """, ("target", step.TargetMessage!.Value.ToString()), ("dest", step.Request.Destination.ToString()));
        using var r = c.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException("找不到已保存的目标邮件或目标邮箱已关闭。已停止清理来源。");
        var raw = (byte[])r[0];
        if (raw.Length != r.GetInt64(1) || r.GetString(2) != step.Sha256 || Convert.ToHexString(SHA256.HashData(raw)) != step.Sha256)
            throw new InvalidDataException("目标原文校验失败。已停止清理来源。");
    }

    internal void Update(Guid id, string phase, string detail)
    {
        using var db = store.Open(); using var c = Command(db,
            "UPDATE transfers SET phase=$phase,detail=$detail WHERE id=$id", ("id", id.ToString()), ("phase", phase), ("detail", detail));
        c.ExecuteNonQuery();
    }

    internal TransferContent ReadLocal(TransferRequest request)
    {
        using var db = store.Open(); using var c = Command(db, """
            SELECT m.raw,m.sha256,m.is_read,coalesce(t.starred,0),coalesce(t.labels,'[]')
            FROM messages m JOIN folders f ON m.folder_id=f.id JOIN accounts a ON a.id=f.account_id
            LEFT JOIN mail_marks t ON t.folder=f.id AND t.item=m.id
            WHERE m.id=$key AND f.id=$source AND a.id=$account AND a.enabled AND NOT a.removed
            """, ("key", request.Key), ("source", request.SourceFolder.ToString()), ("account", request.SourceAccount.ToString()));
        using var r = c.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException("来源邮件已移动、删除或邮箱已关闭。");
        var raw = (byte[])r[0];
        if (Convert.ToHexString(SHA256.HashData(raw)) != r.GetString(1)) throw new InvalidDataException("来源原文校验失败。");
        return new(raw, r.GetBoolean(2), r.GetBoolean(3), JsonSerializer.Deserialize<string[]>(r.GetString(4))!);
    }

    internal void FinishLocalMove(MailTransfer step, CancellationToken token)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction();
        var r = step.Request;
        using var c = Command(db, """
            SELECT m.raw FROM messages m JOIN folders f ON m.folder_id=f.id JOIN accounts a ON a.id=f.account_id
            WHERE m.id=$key AND f.id=$source AND a.id=$account AND a.enabled AND NOT a.removed
            """, ("key", r.Key), ("source", r.SourceFolder.ToString()), ("account", r.SourceAccount.ToString()), ("id", r.Id.ToString()));
        c.Transaction = tx;
        if (c.ExecuteScalar() is not byte[] raw || Convert.ToHexString(SHA256.HashData(raw)) != step.Sha256)
            throw new InvalidDataException("来源邮件已变化，请核对两个目录。已保留目标副本。");
        c.CommandText = """
            DELETE FROM mail_marks WHERE folder=$source AND item=$key;
            DELETE FROM messages WHERE folder_id=$source AND id=$key;
            DELETE FROM mail_move_undo WHERE message=$key;
            UPDATE transfers SET phase='done',detail='目标原文已验证，来源已清理。' WHERE id=$id;
            """;
        token.ThrowIfCancellationRequested(); c.ExecuteNonQuery(); tx.Commit();
    }
}
