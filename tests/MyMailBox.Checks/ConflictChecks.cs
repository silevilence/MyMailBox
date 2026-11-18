using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static class ConflictChecks
{
    public static async Task Run()
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/conflict-" + Guid.NewGuid().ToString("N")));
        var accounts = new AccountService(store); var service = new ProtocolMailService(store);
        var account = await accounts.Save(new(null, "冲突测试", "imap", "alice@localhost"));
        var pin = File.ReadAllText(Path.Combine(PluginChecks.Repository, "artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        await service.Save(account.Id, new(new("127.0.0.1", 3993, "alice@localhost", CertificateSha256: pin)), "local-alice", null);
        using var client = new ImapClient();
        client.ServerCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == pin;
        await client.ConnectAsync("127.0.0.1", 3993, SecureSocketOptions.SslOnConnect); await client.AuthenticateAsync("alice@localhost", "local-alice");
        var path = "conflict-" + Guid.NewGuid().ToString("N");
        var folder = await client.GetFolder(client.PersonalNamespaces[0]).CreateAsync(path, true) ?? throw new Exception("Fixture folder unavailable");
        using var mime = new MimeMessage(); mime.Subject = "相同主题"; mime.Body = new TextPart("plain") { Text = "独立原文" };
        mime.MessageId = "duplicate@example.test"; await folder.AppendAsync(mime); await folder.AppendAsync(mime);
        mime.Headers.Remove(HeaderId.MessageId); await folder.AppendAsync(mime);
        await service.Sync(account.Id, path, 50);
        var page = await service.CachedPage(account.Id, path, 0, 20);
        MailChecks.Check(page.Items.Count == 3 && page.Items.Select(i => i.Uid).Distinct().Count() == 3, "Duplicate or absent Message-ID collided");
        var first = page.Items[0]; var second = page.Items[1]; var third = page.Items[2];
        await service.Queue(account.Id, path, first, MailMark.Star);
        await folder.OpenAsync(FolderAccess.ReadWrite);
        await folder.AddFlagsAsync(new UniqueId(first.Uid), MessageFlags.Seen, true);
        var loaded = await service.OpenMessage(account.Id, path, second, true);
        var transfer = await service.Plan(new(Guid.NewGuid(), account.Id, second.FolderId, second.Uid.ToString(), SqliteMailStore.Inbox, false));
        var archived = await service.Continue(transfer.Request.Id);
        await service.Queue(account.Id, path, second, MailMark.Unread);
        await folder.AddFlagsAsync(new UniqueId(second.Uid), MessageFlags.Deleted, true); await folder.ExpungeAsync(new[] { new UniqueId(second.Uid) });
        var reopened = new ProtocolMailService(store);
        await reopened.Sync(account.Id, path, 50);
        var changes = await reopened.Changes(account.Id);
        var updated = (await reopened.CachedPage(account.Id, path, 0, 20)).Items;
        MailChecks.Check(updated.Single(m => m.Uid == first.Uid) is { IsRead: true, IsStarred: true } &&
            changes.Single(e => e.Uid == first.Uid).State == "done", "Independent server flag overwritten or pending edit not committed");
        MailChecks.Check(updated.Single(m => m.Uid == second.Uid) is { Missing: true, LocalId: not null } &&
            changes.Single(e => e.Uid == second.Uid).State == "conflict", "Remote deletion lost conflict or offline copy");
        var original = await store.Read(loaded.LocalId);
        MailChecks.Check((await reopened.OpenMessage(account.Id, path, second, false)).LocalId == loaded.LocalId &&
            (await store.Read(archived.TargetMessage!.Value)).SequenceEqual(original), "Conflict damaged independent archive");
        await reopened.Queue(account.Id, path, third, MailMark.Read);
        await folder.CloseAsync(false); await folder.RenameAsync(folder.ParentFolder!, path + "-renamed");
        var newFolder = (await ((IMailOrganizer)reopened).Folders(account.Id)).Single(f => f.Path == path + "-renamed");
        MailChecks.Check(newFolder.Id != third.FolderId && (await reopened.Changes(account.Id)).Single(e => e.Uid == third.Uid).State == "conflict",
            "External rename rebound an unverified UID");
        await reopened.Sync(account.Id, newFolder.Path, 50);
        var current = (await reopened.CachedPage(account.Id, newFolder.Path, 0, 20)).Items;
        MailChecks.Check(!current.Single(m => m.Uid == third.Uid).IsRead, "Stale edit applied after external rename");
        await client.DisconnectAsync(true);
        Console.WriteLine("PASS real IMAP conflicts: duplicate and missing Message-ID, independent flag merge, server expunge, detached folder, pending conflicts and retained archive");
    }
}
