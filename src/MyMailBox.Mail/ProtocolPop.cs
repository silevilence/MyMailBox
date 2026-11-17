using System.Security.Cryptography;
using MailKit.Net.Pop3;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class ProtocolMailService
{
    public Task<PopOutcome> ReceivePop(Guid account, bool deleteAfterDownload, int limit = 100, CancellationToken cancellation = default) => ReadOperation(account, async () =>
    {
        var identity = await Account(account, AccountCapabilities.Receive, cancellation);
        if (identity.Provider != "pop3") throw new InvalidDataException("此账户不是 POP3 账户。");
        if (limit is < 1 or > 500) throw new InvalidDataException("每批收取数量须为 1–500。");
        var endpoint = (await Settings(account, cancellation)).Incoming ?? throw new InvalidDataException("请先保存收信配置。");
        var source = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{endpoint.Host.ToLowerInvariant()}:{endpoint.Port}/{endpoint.User}")));
        using var client = new Pop3Client(); await Connect(client, account, false, cancellation);
        IList<string>? uids = client.SupportsUids ? await client.GetMessageUidsAsync(cancellation) : null;
        if (uids is not null && (uids.Any(string.IsNullOrEmpty) || uids.Distinct(StringComparer.Ordinal).Count() != uids.Count)) throw new InvalidDataException("服务器返回重复或空 UIDL，已停止收取并保留来源。");
        if (uids is null && deleteAfterDownload) throw new InvalidDataException("服务器不支持 UIDL；仅允许按原文摘要去重并保留服务器副本，不执行删除。");
        var folder = EnsurePopFolder(account);
        var imported = 0; var existing = 0; var deleted = 0; var pending = false;
        for (var index = 0; index < client.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (uids is not null && HasPopMessage(account, source, uids[index]))
            {
                existing++;
                if (deleteAfterDownload) { await client.DeleteMessageAsync(index, cancellation); deleted++; }
                continue;
            }
            if (imported >= limit) { pending = true; break; }
            if (await client.GetMessageSizeAsync(index, cancellation) > MimeReader.MaxMessageBytes) throw new InvalidDataException("服务器邮件超过 25 MiB，已保留来源；先前下载内容已保存，可调整来源后重试。");
            using var remote = await client.GetStreamAsync(index, cancellationToken: cancellation);
            using var bytes = new MemoryStream(); var buffer = new byte[81920]; int count;
            while ((count = await remote.ReadAsync(buffer, cancellation)) > 0)
            {
                if (bytes.Length + count > MimeReader.MaxMessageBytes) throw new InvalidDataException("邮件超过 25 MiB，已保留服务器副本。");
                bytes.Write(buffer, 0, count);
            }
            var raw = bytes.ToArray(); var uid = uids?[index] ?? "sha256:" + Convert.ToHexString(SHA256.HashData(raw));
            var metadata = await new MimeReader().Parse(raw, cancellation);
            if (SavePopMessage(account, source, uid, folder, raw, metadata, cancellation)) imported++; else existing++;
            if (deleteAfterDownload) { await client.DeleteMessageAsync(index, cancellation); deleted++; }
        }
        // POP deletions commit only on successful QUIT, after local transactions have committed.
        await client.DisconnectAsync(true, cancellation);
        return new PopOutcome(imported, existing, deleted, pending, uids is null ? "已按原文摘要去重，服务器副本全部保留；无 UIDL 时无法区分内容完全相同的独立邮件。" : $"本批新增 {imported} 封，已保存 {existing} 封，服务器删除 {deleted} 封。" + (pending ? "还有邮件，可继续收取。" : ""));
    });
    private Guid EnsurePopFolder(Guid account)
    {
        using var db = _store.Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "CREATE TABLE IF NOT EXISTS pop_receipts(account_id TEXT NOT NULL,source TEXT NOT NULL,uid TEXT NOT NULL,message_id TEXT NOT NULL,PRIMARY KEY(account_id,source,uid)); SELECT id FROM folders WHERE account_id=$id AND role='pop-inbox'";
        command.Parameters.AddWithValue("$id", account.ToString());
        if (command.ExecuteScalar() is string id) { tx.Commit(); return Guid.Parse(id); }
        var folder = Guid.NewGuid(); command.CommandText = "INSERT INTO folders VALUES($folder,$id,'POP3 已下载','POP3 / POP3 已下载','pop-inbox',NULL)"; command.Parameters.AddWithValue("$folder", folder.ToString()); command.ExecuteNonQuery(); tx.Commit(); return folder;
    }
    private bool HasPopMessage(Guid account, string source, string uid)
    {
        using var db = _store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT raw,sha256 FROM messages JOIN pop_receipts ON message_id=messages.id WHERE account_id=$account AND source=$source AND uid=$uid";
        command.Parameters.AddWithValue("$account", account.ToString()); command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$uid", uid);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        if (Convert.ToHexString(SHA256.HashData((byte[])reader[0])) != reader.GetString(1)) throw new InvalidDataException("本地副本校验失败，已停止收取并保留服务器来源，请先修复本地存储。");
        return true;
    }
    private bool SavePopMessage(Guid account, string source, string uid, Guid folder, byte[] raw, ParsedMail mail, CancellationToken token)
    {
        using var db = _store.Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT message_id FROM pop_receipts WHERE account_id=$account AND source=$source AND uid=$uid";
        command.Parameters.AddWithValue("$account", account.ToString()); command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$uid", uid);
        if (command.ExecuteScalar() is string known)
        {
            command.CommandText = "SELECT count(*) FROM messages WHERE id=$known"; command.Parameters.AddWithValue("$known", known);
            if (Convert.ToInt32(command.ExecuteScalar()) != 0) { tx.Commit(); return false; }
            command.CommandText = "DELETE FROM pop_receipts WHERE account_id=$account AND source=$source AND uid=$uid"; command.ExecuteNonQuery();
        }
        var id = Guid.NewGuid();
        command.CommandText = """
            INSERT INTO messages(id,folder_id,subject,sender,recipient,date,size,raw,sha256) VALUES($id,$folder,$subject,$from,$to,$date,$size,$raw,$hash);
            INSERT INTO pop_receipts VALUES($account,$source,$uid,$id);
            """;
        command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$folder", folder.ToString()); command.Parameters.AddWithValue("$subject", mail.Subject);
        command.Parameters.AddWithValue("$from", mail.From); command.Parameters.AddWithValue("$to", mail.To); command.Parameters.AddWithValue("$date", mail.Date.ToUniversalTime().ToString("O")); command.Parameters.AddWithValue("$size", raw.Length);
        command.Parameters.AddWithValue("$raw", raw); command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(raw)));
        token.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit(); return true;
    }
}
