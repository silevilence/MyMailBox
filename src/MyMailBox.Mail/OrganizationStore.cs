using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Text.Json;

namespace MyMailBox.Mail;

internal sealed class OrganizationStore(SqliteMailStore store)
{
    internal static void Initialize(SqliteMailStore store)
    {
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS mail_marks(folder TEXT NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                item TEXT NOT NULL,starred INTEGER NOT NULL DEFAULT 0,labels TEXT NOT NULL DEFAULT '[]',PRIMARY KEY(folder,item))
            """;
        command.ExecuteNonQuery();
    }

    internal static string FolderName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 120 || name.Any(c => c is '/' or '\\' || char.IsControl(c)))
            throw new InvalidDataException("目录名称需为 1–120 字，不能包含斜线或控制字符。");
        return name;
    }

    internal static bool CanRename(MailFolder folder) => folder.Role is "archive" or "custom" or "imported";

    internal void RenameLocal(MailFolder folder, string name)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", folder.Id.ToString());
        command.Parameters.AddWithValue("$account", folder.AccountId.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.CommandText = """
            SELECT COUNT(*) FROM folders WHERE account_id=$account AND parent_id IS (SELECT parent_id FROM folders WHERE id=$id)
              AND name=$name COLLATE NOCASE AND id<>$id
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) > 0) throw new InvalidDataException("同一层已存在同名目录。");
        command.CommandText = "SELECT path FROM folders WHERE id=$id";
        var old = command.ExecuteScalar() as string ?? throw new InvalidDataException("目录已删除。");
        var path = old[..(old.LastIndexOf(" / ", StringComparison.Ordinal) + 3)] + name;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$old", old);
        command.CommandText = """
            WITH RECURSIVE descendants(id) AS (SELECT id FROM folders WHERE parent_id=$id
                UNION ALL SELECT f.id FROM folders f JOIN descendants d ON f.parent_id=d.id)
            UPDATE folders SET path=$path || substr(path,length($old)+1) WHERE id IN descendants;
            UPDATE folders SET name=$name,path=$path WHERE id=$id;
            """;
        command.ExecuteNonQuery(); tx.Commit();
    }

    internal void DeleteLocal(MailFolder folder)
    {
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            DELETE FROM folders WHERE id=$id AND NOT EXISTS(SELECT 1 FROM messages WHERE folder_id=$id)
                AND NOT EXISTS(SELECT 1 FROM folders WHERE parent_id=$id)
            """;
        command.Parameters.AddWithValue("$id", folder.Id.ToString());
        if (command.ExecuteNonQuery() == 0) throw new InvalidDataException("只能删除空目录，请先移出邮件和子目录。");
    }

    internal ManagedPage LocalMessages(Guid folder, int offset, int limit)
    {
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT m.id,m.subject,m.is_read,coalesce(t.starred,0),coalesce(t.labels,'[]')
            FROM messages m LEFT JOIN mail_marks t ON t.folder=m.folder_id AND t.item=m.id
            WHERE m.folder_id=$folder ORDER BY m.date DESC,m.id LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$folder", folder.ToString());
        command.Parameters.AddWithValue("$limit", limit + 1); command.Parameters.AddWithValue("$offset", offset);
        using var r = command.ExecuteReader();
        var rows = new List<ManagedMessage>();
        while (r.Read()) rows.Add(new(r.GetString(0),r.GetString(1),r.GetBoolean(2),r.GetBoolean(3),JsonSerializer.Deserialize<string[]>(r.GetString(4))!));
        return new(rows.Take(limit).ToArray(), rows.Count > limit);
    }

    internal string[] Labels(Guid folder, string key)
    {
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT labels FROM mail_marks WHERE folder=$folder AND item=$key";
        command.Parameters.AddWithValue("$folder",folder.ToString()); command.Parameters.AddWithValue("$key",key);
        return JsonSerializer.Deserialize<string[]>(command.ExecuteScalar() as string ?? "[]")!;
    }

    internal ManagedMessage Mark(Guid folder, string key, ManagedMessage? remote, MailMark mark, string label)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var c = db.CreateCommand(); c.Transaction = tx;
        c.Parameters.AddWithValue("$folder", folder.ToString()); c.Parameters.AddWithValue("$key", key);
        bool read, star;
        string subject;
        if (remote is null)
        {
            c.CommandText = """
                SELECT m.subject,m.is_read,coalesce(t.starred,0) FROM messages m LEFT JOIN mail_marks t ON t.folder=m.folder_id AND t.item=m.id
                WHERE m.id=$key AND m.folder_id=$folder
                """;
            using var r = c.ExecuteReader();
            if (!r.Read()) throw new InvalidDataException("邮件已移出或删除。");
            subject = r.GetString(0); read = r.GetBoolean(1); star = r.GetBoolean(2);
        }
        else { subject = remote.Subject; read = remote.Read; star = remote.Starred; }
        c.CommandText = "SELECT labels FROM mail_marks WHERE folder=$folder AND item=$key";
        var labels = JsonSerializer.Deserialize<string[]>(c.ExecuteScalar() as string ?? "[]")!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        switch (mark)
        {
            case MailMark.Read: case MailMark.Unread: read = mark == MailMark.Read; break;
            case MailMark.Star: case MailMark.Unstar: star = mark == MailMark.Star; break;
            case MailMark.AddLabel: labels.Add(label); break;
            case MailMark.RemoveLabel: labels.Remove(label); break;
        }
        var sorted = labels.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        c.CommandText = """
            INSERT INTO mail_marks(folder,item,starred,labels) VALUES($folder,$key,$star,$labels)
              ON CONFLICT(folder,item) DO UPDATE SET starred=$star,labels=$labels
            """;
        c.Parameters.AddWithValue("$star",star); c.Parameters.AddWithValue("$labels",JsonSerializer.Serialize(sorted));
        c.ExecuteNonQuery();
        if (mark is MailMark.Read or MailMark.Unread)
        {
            c.CommandText = remote is null ? "UPDATE messages SET is_read=$read WHERE id=$key AND folder_id=$folder" :
                "UPDATE messages SET is_read=$read WHERE id=(SELECT message FROM imap_summaries WHERE folder=$folder AND uid=$key)";
            c.Parameters.AddWithValue("$read",read); c.ExecuteNonQuery();
        }
        tx.Commit(); return new(key,subject,read,star,sorted);
    }
}
