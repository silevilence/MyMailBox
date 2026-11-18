using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

internal sealed class LocalMailActions(SqliteMailStore store)
{
    internal static void Initialize(SqliteMailStore store)
    {
        using var db = store.Open(); using var c = db.CreateCommand();
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS mail_move_undo(id TEXT PRIMARY KEY,account TEXT NOT NULL,
                source TEXT NOT NULL,destination TEXT NOT NULL,message TEXT NOT NULL UNIQUE);
            """;
        c.ExecuteNonQuery();
    }

    internal Guid Special(MailAccount account, MailAction action)
    {
        string role = action == MailAction.Trash ? "trash" : "archive-system";
        string name = action == MailAction.Trash ? "垃圾箱" : "归档";
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.Parameters.AddWithValue("$account",account.Id.ToString()); c.Parameters.AddWithValue("$role",role);
        c.CommandText = "SELECT id FROM folders WHERE account_id=$account AND role=$role";
        if (c.ExecuteScalar() is string existing) return Guid.Parse(existing);
        var id = Guid.NewGuid();
        c.CommandText = "INSERT INTO folders VALUES($id,$account,$name,$path,$role,NULL)";
        c.Parameters.AddWithValue("$id",id.ToString()); c.Parameters.AddWithValue("$name",name); c.Parameters.AddWithValue("$path",account.Name+" / "+name);
        c.ExecuteNonQuery(); tx.Commit(); return id;
    }

    internal MailActionResult Apply(MailActionRequest request, string key, Guid? destination, CancellationToken token)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.Parameters.AddWithValue("$account",request.Account.ToString()); c.Parameters.AddWithValue("$source",request.Source.ToString());
        c.Parameters.AddWithValue("$key",key);
        c.CommandText = """
            SELECT count(*) FROM messages m JOIN folders f ON m.folder_id=f.id JOIN accounts a ON f.account_id=a.id
            WHERE a.id=$account AND a.enabled AND NOT a.removed AND m.id=$key AND f.id=$source
            """;
        if (Convert.ToInt32(c.ExecuteScalar()) != 1) throw new InvalidDataException("来源邮件已移出或目录不可用。");
        string? targetKey = null;
        Guid? undo = null;
        if (request.Action == MailAction.DeletePermanently)
        {
            c.CommandText = """
                DELETE FROM mail_marks WHERE folder=$source AND item=$key;
                DELETE FROM mail_move_undo WHERE message=$key;
                DELETE FROM messages WHERE folder_id=$source AND id=$key;
                """;
        }
        else
        {
            if (destination is null || destination == request.Source) throw new InvalidDataException("请选择同一账户内的其他目录。");
            c.Parameters.AddWithValue("$dest",destination.Value.ToString());
            c.CommandText = "SELECT count(*) FROM folders WHERE account_id=$account AND id=$dest";
            if (Convert.ToInt32(c.ExecuteScalar()) != 1) throw new InvalidDataException("目标目录不存在或属于其他账户。");
            if (request.Action == MailAction.Copy)
            {
                targetKey = Guid.NewGuid().ToString();
                c.Parameters.AddWithValue("$new",targetKey);
                c.CommandText = """
                    INSERT INTO messages(id,folder_id,subject,sender,recipient,date,size,is_read,raw,sha256)
                        SELECT $new,$dest,subject,sender,recipient,date,size,is_read,raw,sha256 FROM messages WHERE id=$key;
                    INSERT INTO mail_marks(folder,item,starred,labels)
                        SELECT $dest,$new,starred,labels FROM mail_marks WHERE folder=$source AND item=$key;
                    """;
            }
            else
            {
                undo = request.OperationId ?? Guid.NewGuid(); targetKey = key;
                c.Parameters.AddWithValue("$undo",undo.ToString());
                c.CommandText = """
                    UPDATE messages SET folder_id=$dest WHERE id=$key;
                    UPDATE mail_marks SET folder=$dest WHERE folder=$source AND item=$key;
                    INSERT INTO mail_move_undo VALUES($undo,$account,$source,$dest,$key)
                      ON CONFLICT(message) DO UPDATE SET id=$undo,account=$account,source=$source,destination=$dest;
                    """;
            }
        }
        token.ThrowIfCancellationRequested(); c.ExecuteNonQuery(); tx.Commit();
        var detail = request.Action switch
        {
            MailAction.Copy => "已复制完整本地副本", MailAction.Move => "已移动", MailAction.Archive => "已归档",
            MailAction.Trash => "已移入垃圾箱", _ => "已永久删除本地邮件"
        };
        return new(key,true,detail,targetKey,undo);
    }

    internal void Undo(Guid operation, CancellationToken token)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.Parameters.AddWithValue("$id",operation.ToString());
        c.CommandText = """
            SELECT u.source,u.destination,u.message FROM mail_move_undo u
              JOIN messages m ON m.id=u.message AND m.folder_id=u.destination
              JOIN folders f ON f.id=u.source AND f.account_id=u.account JOIN accounts a ON a.id=u.account
              WHERE u.id=$id AND a.enabled AND NOT a.removed
            """;
        using (var r = c.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidDataException("邮件已变化或原目录已移除，不能撤销这次移动。");
            c.Parameters.AddWithValue("$source",r.GetString(0)); c.Parameters.AddWithValue("$dest",r.GetString(1));
            c.Parameters.AddWithValue("$key",r.GetString(2));
        }
        c.CommandText = """
            UPDATE messages SET folder_id=$source WHERE id=$key AND folder_id=$dest;
            UPDATE mail_marks SET folder=$source WHERE folder=$dest AND item=$key;
            DELETE FROM mail_move_undo WHERE id=$id;
            """;
        token.ThrowIfCancellationRequested(); c.ExecuteNonQuery(); tx.Commit();
    }
}
