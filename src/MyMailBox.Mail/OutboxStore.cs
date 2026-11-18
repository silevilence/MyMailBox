using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Text.Json;

namespace MyMailBox.Mail;

internal sealed class OutboxStore(SqliteMailStore store)
{
    private const string Columns = "id,account,subject,state,detail,accepted,remaining,created,copy_pending";
    internal static void Initialize(SqliteMailStore store)
    {
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS outbox(
                id TEXT PRIMARY KEY, account TEXT NOT NULL REFERENCES accounts(id), subject TEXT NOT NULL,
                raw BLOB NOT NULL, request_hash TEXT NOT NULL, accepted TEXT NOT NULL DEFAULT '[]', remaining TEXT NOT NULL,
                state TEXT NOT NULL, detail TEXT NOT NULL, created INTEGER NOT NULL, due INTEGER NOT NULL,
                copy_pending INTEGER NOT NULL DEFAULT 0, local_copy TEXT);
            CREATE INDEX IF NOT EXISTS outbox_due ON outbox(state,due);
            CREATE TABLE IF NOT EXISTS outbox_events(
                sequence INTEGER PRIMARY KEY, item TEXT NOT NULL REFERENCES outbox(id), time INTEGER NOT NULL, detail TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS outbox_events_item ON outbox_events(item,sequence);
            CREATE TRIGGER IF NOT EXISTS outbox_added AFTER INSERT ON outbox BEGIN
                INSERT INTO outbox_events(item,time,detail) VALUES(NEW.id,unixepoch(),NEW.detail);
            END;
            CREATE TRIGGER IF NOT EXISTS outbox_changed AFTER UPDATE ON outbox
                WHEN NEW.state<>OLD.state OR NEW.detail<>OLD.detail OR NEW.copy_pending<>OLD.copy_pending BEGIN
                INSERT INTO outbox_events(item,time,detail) VALUES(NEW.id,unixepoch(),NEW.detail);
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Key, object Value)[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue("$" + key, value);
        return command;
    }

    private int Change(string sql, params (string, object)[] args)
    {
        using var db = store.Open();
        using var command = Command(db, sql, args);
        return command.ExecuteNonQuery();
    }

    internal void Enqueue(Guid id, Guid account, string subject, byte[] raw, string[] recipients, string requestHash)
    {
        var inserted = Change("""
            INSERT INTO outbox(id,account,subject,raw,request_hash,remaining,state,detail,created,due)
            VALUES($id,$account,$subject,$raw,$hash,$remaining,'pending','已加入发件箱。',unixepoch(),unixepoch())
            ON CONFLICT(id) DO NOTHING
            """, ("id", id.ToString()), ("account", account.ToString()), ("subject", subject),
            ("raw", raw), ("hash", requestHash), ("remaining", JsonSerializer.Serialize(recipients)));
        if (inserted == 0)
        {
            using var db = store.Open();
            using var verify = Command(db, "SELECT request_hash FROM outbox WHERE id=$id AND account=$account",
                ("id", id.ToString()), ("account", account.ToString()));
            if (verify.ExecuteScalar() as string != requestHash) throw new InvalidDataException("该发件记录已用于另一封邮件，请新建邮件。");
        }
    }

    private static OutboxItem Read(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)),
        r.GetString(2), r.GetString(3), r.GetString(4), JsonSerializer.Deserialize<string[]>(r.GetString(5))!,
        JsonSerializer.Deserialize<string[]>(r.GetString(6))!, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7)), r.GetBoolean(8));

    internal OutboxItem Get(Guid id)
    {
        using var db = store.Open();
        using var command = Command(db, $"SELECT {Columns} FROM outbox WHERE id=$id", ("id", id.ToString()));
        using var r = command.ExecuteReader();
        return r.Read() ? Read(r) : throw new InvalidDataException("发件记录不存在。");
    }

    internal byte[] Raw(Guid id)
    {
        using var db = store.Open();
        using var command = Command(db, "SELECT raw FROM outbox WHERE id=$id", ("id", id.ToString()));
        return command.ExecuteScalar() as byte[] ?? throw new InvalidDataException("找不到待发送原文。");
    }

    internal IReadOnlyList<OutboxItem> List(Guid account, int offset, int limit)
    {
        using var db = store.Open();
        using var command = Command(db, $"SELECT {Columns} FROM outbox WHERE account=$account ORDER BY created DESC,rowid DESC LIMIT $limit OFFSET $offset",
            ("account", account.ToString()), ("offset", offset), ("limit", limit));
        using var r = command.ExecuteReader();
        var rows = new List<OutboxItem>();
        while (r.Read()) rows.Add(Read(r));
        return rows;
    }

    internal IReadOnlyList<string> History(Guid id)
    {
        using var db = store.Open();
        using var command = Command(db, "SELECT time,detail FROM outbox_events WHERE item=$id ORDER BY sequence DESC LIMIT 100", ("id", id.ToString()));
        using var r = command.ExecuteReader();
        var events = new List<string>();
        while (r.Read()) events.Add($"{DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime:g}  {r.GetString(1)}");
        return events;
    }

    internal Guid? Next()
    {
        using var db = store.Open();
        using var command = Command(db, """
            SELECT id FROM outbox WHERE state='sending' OR (state='pending' AND due<=unixepoch())
            ORDER BY due,rowid LIMIT 1
            """);
        return command.ExecuteScalar() is string id ? Guid.Parse(id) : null;
    }

    internal void RecoverInterrupted() => Change("""
        UPDATE outbox SET state='unknown',detail='上次发送被中断，结果待确认。请核对收件情况，系统不会自动重发。'
        WHERE state='sending'
        """);
    internal bool Claim(Guid id) => Change("""
        UPDATE outbox SET state='sending',detail='正在发送，等待服务器回执。' WHERE id=$id AND state='pending'
        """, ("id", id.ToString())) == 1;
    internal void BeforeSubmissionFailure(Guid id, bool retry, string detail) => Change("""
        UPDATE outbox SET state=$state,detail=$detail,due=unixepoch()+60 WHERE id=$id AND state='pending'
        """, ("id", id.ToString()), ("state", retry ? "pending" : "rejected"), ("detail", detail));

    internal void Record(OutboxItem item, SendOutcome attempt)
    {
        var accepted = item.Accepted.Concat(attempt.Accepted).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var remaining = item.Remaining.Except(attempt.Accepted, StringComparer.OrdinalIgnoreCase).ToArray();
        string state = attempt.State == "rejected" && accepted.Length > 0 ? "partial" : attempt.State;
        Change("""
            UPDATE outbox SET state=$state,detail=$detail,accepted=$accepted,remaining=$remaining,
                copy_pending=CASE WHEN $copy=1 AND local_copy IS NULL THEN 1 ELSE copy_pending END
            WHERE id=$id AND state='sending'
            """, ("id", item.Id.ToString()), ("state", state), ("detail", attempt.Message), ("accepted", JsonSerializer.Serialize(accepted)),
            ("remaining", JsonSerializer.Serialize(remaining)), ("copy", attempt.Accepted.Count > 0 ? 1 : 0));
    }

    internal void Cancel(Guid id)
    {
        if (Change("""
            UPDATE outbox SET state='canceled',detail='已取消尚未发送的收件人。' WHERE id=$id AND state='pending'
            """, ("id", id.ToString())) != 1) throw new InvalidDataException("仅能取消尚未提交的邮件。请刷新状态。");
    }

    internal void Retry(Guid id)
    {
        if (Change("""
            UPDATE outbox SET state='pending',due=unixepoch(),detail='已安排重试尚未接收的收件人。'
            WHERE id=$id AND state IN ('rejected','partial','pending')
            """, ("id", id.ToString())) != 1) throw new InvalidDataException("此状态不能安全重试。请先核对投递结果。");
    }

    internal void SaveCopy(OutboxItem item, byte[] raw, ParsedMail mail, CancellationToken token)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var command = Command(db, "SELECT copy_pending FROM outbox WHERE id=$id", ("id", item.Id.ToString()));
        command.Transaction = tx;
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) return;
        command.CommandText = "SELECT id FROM folders WHERE account_id=$account AND role='local-sent-copy'";
        command.Parameters.AddWithValue("$account", item.Account.ToString());
        var folder = command.ExecuteScalar() as string;
        if (folder is null)
        {
            folder = Guid.NewGuid().ToString();
            command.CommandText = """
                INSERT INTO folders SELECT $folder,id,'已发送（本地副本）',name || ' / 已发送（本地副本）','local-sent-copy',NULL
                FROM accounts WHERE id=$account
                """;
            command.Parameters.AddWithValue("$folder", folder);
            command.ExecuteNonQuery();
        }
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        var copy = SqliteMailStore.InsertMessage(insert, Guid.Parse(folder), raw, mail);
        command.CommandText = "UPDATE outbox SET copy_pending=0,local_copy=$copy WHERE id=$id";
        command.Parameters.AddWithValue("$copy", copy.ToString());
        command.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        tx.Commit();
    }
}
