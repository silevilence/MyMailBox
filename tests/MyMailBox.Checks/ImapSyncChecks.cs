using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static partial class ProtocolChecks
{
    private sealed class BatchProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    public static async Task ImapSync()
    {
        var pin = File.ReadAllText(Path.Combine(PluginChecks.Repository, "artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/imap-sync-" + Guid.NewGuid().ToString("N")));
        var service = new ProtocolMailService(store);
        var account = await new AccountService(store).Save(new(null, "离线验收", "imap", "alice@localhost"));
        var endpoint = new MailEndpoint("127.0.0.1", 3993, "alice@localhost", CertificateSha256: pin);
        await service.Save(account.Id, new(endpoint), "local-alice", null);
        using var writer = new ImapClient();
        writer.ServerCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == pin;
        await writer.ConnectAsync("127.0.0.1", 3993, SecureSocketOptions.SslOnConnect);
        await writer.AuthenticateAsync("alice@localhost", "local-alice");
        var remote = await writer.GetFolder(writer.PersonalNamespaces[0]).CreateAsync("sync-" + Guid.NewGuid().ToString("N"), true);
        if (remote is null) throw new Exception("GreenMail did not create fixture folder");
        var path = remote.FullName;
        var mime = new BodyBuilder { TextBody = "可离线阅读" };
        mime.Attachments.Add("附件.txt", "attachment"u8.ToArray());
        using var sample = new MimeMessage();
        sample.From.Add(MailboxAddress.Parse("alice@localhost"));
        sample.Body = mime.ToMessageBody();
        for (int i = 0; i < 40; i++)
        {
            sample.Subject = $"测试 {i:D2}";
            await remote.AppendAsync(sample);
        }
        var initial = await service.Sync(account.Id, path, 5);
        var first = (await service.CachedPage(account.Id, path, 0, 3)).Items;
        MailChecks.Check(initial.Added == 5 && first[0].Subject == "测试 39" && first.All(m => m.LocalId is null), "Recent window downloaded bodies or selected wrong messages");
        var search = new MailSearchService(store,service);
        var results = await search.Search(new([new("subject",SearchMatch.Equals,"测试 39")]));
        MailChecks.Check(results.Summaries==5 && results.Items.Single().LocalId is null,"IMAP-only search coverage missing");
        await Reject(() => service.OpenMessage(account.Id, path, first[0], false), "尚未缓存");
        var opened = await service.OpenMessage(account.Id, path, first[0], true);
        var after = await search.Search(new([new("subject",SearchMatch.Equals,"测试 39")]));
        MailChecks.Check(after.Summaries==4 && after.Items.Single().Id==results.Items[0].Id && (await search.Read(results.Items[0])).Contains("可离线"),"Cached IMAP hit duplicated or lost stable location");
        var parsed = await new MyMailBox.Mail.MimeReader().Parse(await store.Read(opened.LocalId));
        MailChecks.Check(opened.Text.Contains("可离线") && parsed.Parts!.Any(p => p.Name == "附件.txt"), "Cached MIME lost text or attachment");
        var reopened = new ProtocolMailService(store);
        await reopened.Save(account.Id, new(endpoint), "incorrect", null);
        MailChecks.Check((await reopened.OpenMessage(account.Id, path, first[0], false)).LocalId == opened.LocalId, "Offline read used the network");
        await Reject(() => reopened.Sync(account.Id, path, 5), "认证");
        await reopened.Save(account.Id, new(endpoint), "local-alice", null);
        MailChecks.Check((await reopened.Sync(account.Id, path, 5)).Added == 0, "Restart duplicated summaries");
        using var cancel = new CancellationTokenSource();
        var batch = new BatchProgress(_ => cancel.Cancel());
        try { await service.Sync(account.Id, path, 100, batch, cancel.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        var afterCancel = await service.CachedPage(account.Id, path, 0, 100);
        MailChecks.Check(afterCancel.Items.Count == 32, "Canceled chunk did not persist");
        var expanded = await service.Sync(account.Id, path, 100);
        MailChecks.Check(expanded.Added == 8, "Expanded window skipped or repeated a chunk");
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        var wait = service.WaitForChange(account.Id, path, expanded, timer.Token);
        sample.Subject = "新到邮件"; await remote.AppendAsync(sample);
        await wait;
        MailChecks.Check((await service.Sync(account.Id, path, 100)).Added == 1, "Incremental sync missed new arrival");
        await remote.OpenAsync(FolderAccess.ReadWrite);
        await remote.AddFlagsAsync(new UniqueId(first[1].Uid), MessageFlags.Deleted, true);
        await remote.ExpungeAsync();
        await Reject(() => service.OpenMessage(account.Id, path, first[1], true), "不存在");
        MailChecks.Check((await service.CachedPage(account.Id, path, 0, 100)).Items.Single(m => m.Uid == first[1].Uid).Missing, "Remote deletion reported as uncached");
        // A stale stored validity simulates reopening a rebuilt server folder on the next SELECT.
        using (var db = store.Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE imap_folders SET validity=validity+1";
            command.ExecuteNonQuery();
        }
        var reset = await service.Sync(account.Id, path, 5);
        MailChecks.Check(reset.Added == 5 && (await store.Read(opened.LocalId)).Length > 0, "Generation reset reused old UIDs or removed cached MIME");
        await Reject(() => service.OpenMessage(account.Id, path, first[0], true), "目录标识");
        await writer.DisconnectAsync(true);
        Console.WriteLine("PASS real IMAP: recent-first, partial commit/cancel/resume, incremental arrival, generation rebuild, missing vs uncached, offline MIME and attachment after restart");
    }
}
