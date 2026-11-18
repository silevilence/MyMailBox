using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static partial class TransferChecks
{
    public static async Task Live()
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/cross-" + Guid.NewGuid().ToString("N")));
        var accounts = new AccountService(store);
        var account = await accounts.Save(new(null, "远端", "imap", "alice@localhost"));
        var service = new ProtocolMailService(store);
        var pin = File.ReadAllText(Path.Combine(PluginChecks.Repository, "artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        await service.Save(account.Id, new(new("127.0.0.1", 3993, "alice@localhost", CertificateSha256: pin)), "local-alice", null);
        var path = "cross-" + Guid.NewGuid().ToString("N");
        var source = await service.Create(account.Id, null, path);
        using var client = new ImapClient();
        client.ServerCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == pin;
        await client.ConnectAsync("127.0.0.1", 3993, SecureSocketOptions.SslOnConnect); await client.AuthenticateAsync("alice@localhost", "local-alice");
        var folder = await client.GetFolderAsync(path);
        var builder = new BodyBuilder { TextBody = "完整原文与附件" }; builder.Attachments.Add("附件.bin", new byte[] { 0, 255, 3, 9 });
        using var mime = new MimeMessage(); mime.Subject = "迁移"; mime.Body = builder.ToMessageBody();
        await folder.AppendAsync(mime); await folder.AppendAsync(mime);
        var messages = await service.Messages(account.Id, source, 0, 20);
        TransferRequest Make(string key, bool move) => new(Guid.NewGuid(), account.Id, source, key, SqliteMailStore.Inbox, move);
        var copy = Make(messages.Items[0].Key, false);
        await service.Plan(copy);
        void Sql(string sql)
        {
            using var db = store.Open(); using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery();
        }
        Sql("CREATE TRIGGER full_disk BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT,'full disk'); END");
        var failed = await service.Continue(copy.Id);
        MailChecks.Check(failed.Phase == "planned" && failed.TargetMessage is null && (await service.Messages(account.Id, source, 0, 20)).Items.Count == 2,
            "Failed target write changed source");
        Sql("DROP TRIGGER full_disk");
        var copied = await service.Continue(copy.Id);
        MailChecks.Check(copied.Phase == "done" && copied.TargetMessage.HasValue, "Copy did not complete: " + copied.Detail);
        var parsed = await new MyMailBox.Mail.MimeReader().Parse(await store.Read(copied.TargetMessage!.Value));
        MailChecks.Check(parsed.Text.Contains("完整原文") && parsed.Parts!.Any(p => p.Name == "附件.bin"), "Target MIME incomplete");
        var move = Make(messages.Items[1].Key, true); await service.Plan(move);
        var limited = new ProtocolMailService(store, () =>
        {
            var c = new ImapClient(); c.Authenticated += (_, _) => c.Capabilities &= ~ImapCapabilities.UidPlus; return c;
        });
        var pending = await limited.Continue(move.Id);
        MailChecks.Check(pending.Phase == "cleaning" && pending.TargetMessage.HasValue && (await service.Messages(account.Id, source, 0, 20)).Items.Count == 2,
            "Unverified cleanup was attempted");
        var completed = await new ProtocolMailService(store).Continue(move.Id);
        MailChecks.Check(completed.Phase == "done" && completed.TargetMessage == pending.TargetMessage &&
            (await service.Messages(account.Id, source, 0, 20)).Items.Count == 1, "Resume duplicated target or failed source cleanup");
        var again = await service.Continue(move.Id);
        MailChecks.Check(again.TargetMessage == completed.TargetMessage && (await store.List(SqliteMailStore.Inbox, 0, 20)).Items.Count == 2, "Replay duplicated target");
        try { await service.Plan(move with { Id = Guid.NewGuid(), Destination = source }); throw new Exception("Unexpected remote target acceptance"); }
        catch (InvalidDataException) { }
        await client.DisconnectAsync(true);
        Console.WriteLine("PASS cross-store real IMAP: full MIME, failed write retains source, target hash verification, unsupported cleanup retained, restart idempotence, local-only importer capability");
    }
}
