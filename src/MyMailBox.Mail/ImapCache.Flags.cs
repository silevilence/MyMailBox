using MailKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

internal sealed partial class ImapCache
{
    internal void Queue(string path, ImapCachedMail item, MailMark mark)
    {
        if (mark is not (MailMark.Read or MailMark.Unread or MailMark.Star or MailMark.Unstar))
            throw new InvalidDataException("离线操作只支持已读与星标。");
        using var db = store.Open(); using var tx = db.BeginTransaction();
        using var c = FindMessage(db, path, item, tx);
        using (var r = c.ExecuteReader())
            if (!r.Read() || r.GetBoolean(1)) throw new InvalidDataException("来源邮件已移除或目录标识失效，无法安排修改。");
        var seen = mark is MailMark.Read or MailMark.Unread;
        c.Parameters.AddWithValue("$a", (int)(seen ? MailMark.Read : MailMark.Star));
        c.Parameters.AddWithValue("$b", (int)(seen ? MailMark.Unread : MailMark.Unstar));
        c.Parameters.AddWithValue("$mark", (int)mark); c.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        c.CommandText = """
            DELETE FROM imap_edits WHERE folder=$folder AND uid=$uid AND mark IN ($a,$b) AND state='pending';
            INSERT INTO imap_edits SELECT $id,$account,$folder,$uid,$path,subject,$mark,'pending','等待下一次同步'
              FROM imap_summaries WHERE folder=$folder AND uid=$uid;
            """;
        c.ExecuteNonQuery(); tx.Commit();
    }

    internal IReadOnlyList<OfflineMark> Edits(Guid? folder = null)
    {
        using var db = store.Open(); using var c = Lookup(db, "");
        c.CommandText = folder is null ?
            "SELECT id,path,subject,uid,mark,state,detail FROM imap_edits WHERE account=$account ORDER BY (state<>'done') DESC,rowid DESC" :
            "SELECT id,path,subject,uid,mark,state,detail FROM imap_edits WHERE account=$account AND folder=$folder AND state='pending' ORDER BY rowid";
        if (folder.HasValue) c.Parameters.AddWithValue("$folder", folder.Value.ToString());
        using var r = c.ExecuteReader(); var rows = new List<OfflineMark>();
        while (r.Read()) rows.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), (uint)r.GetInt64(3), (MailMark)r.GetInt32(4), r.GetString(5), r.GetString(6)));
        return rows;
    }

    internal void Dismiss(Guid edit)
    {
        using var db = store.Open(); using var c = Lookup(db, "");
        c.CommandText = "DELETE FROM imap_edits WHERE account=$account AND id=$edit AND state<>'pending'"; c.Parameters.AddWithValue("$edit", edit.ToString()); c.ExecuteNonQuery();
    }

    internal void Result(OfflineMark edit, string state, string detail)
    {
        using var db = store.Open(); using var c = db.CreateCommand();
        c.CommandText = "UPDATE imap_edits SET state=$state,detail=$detail WHERE id=$edit AND state='pending'";
        c.Parameters.AddWithValue("$edit", edit.Id.ToString()); c.Parameters.AddWithValue("$state", state); c.Parameters.AddWithValue("$detail", detail); c.ExecuteNonQuery();
    }

    internal UniqueId[] Observed(Folder folder, int range)
    {
        using var db = store.Open(); using var c = db.CreateCommand();
        c.CommandText = """
            SELECT uid FROM (SELECT uid FROM imap_summaries WHERE folder=$folder AND missing=0 ORDER BY uid DESC LIMIT $range)
            UNION SELECT uid FROM imap_edits WHERE folder=$folder AND state='pending'
            """;
        c.Parameters.AddWithValue("$folder", folder.Id.ToString()); c.Parameters.AddWithValue("$range", range);
        using var r = c.ExecuteReader(); var result = new List<UniqueId>();
        while (r.Read()) result.Add(new((uint)r.GetInt64(0)));
        return result.ToArray();
    }

    internal void Snapshot(Guid folder, UniqueId[] requested, IList<IMessageSummary> observed)
    {
        var byUid = observed.Where(s => s.Flags.HasValue).ToDictionary(s => s.UniqueId, s => s.Flags!.Value);
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.Parameters.AddWithValue("$folder", folder.ToString());
        var uid = c.Parameters.Add("$uid", Microsoft.Data.Sqlite.SqliteType.Integer);
        var flags = c.Parameters.Add("$flags", Microsoft.Data.Sqlite.SqliteType.Integer);
        var read = c.Parameters.Add("$read", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var id in requested)
        {
            uid.Value = (long)id.Id;
            if (byUid.TryGetValue(id, out var value))
            {
                flags.Value = (int)value; read.Value = value.HasFlag(MessageFlags.Seen);
                c.CommandText = """
                    UPDATE imap_summaries SET flags=$flags WHERE folder=$folder AND uid=$uid AND flags<>$flags;
                    UPDATE messages SET is_read=$read WHERE id=(SELECT message FROM imap_summaries WHERE folder=$folder AND uid=$uid) AND is_read<>$read;
                    """;
            }
            else
            {
                // Disappearance invalidates pending server writes. Cached MIME and independent archives remain available.
                flags.Value = 0; read.Value = 0;
                c.CommandText = """
                    UPDATE imap_summaries SET missing=1 WHERE folder=$folder AND uid=$uid;
                    UPDATE imap_edits SET state='conflict',detail='来源已移动或删除；离线修改未应用到其他邮件。'
                      WHERE folder=$folder AND uid=$uid AND state='pending';
                    """;
            }
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
