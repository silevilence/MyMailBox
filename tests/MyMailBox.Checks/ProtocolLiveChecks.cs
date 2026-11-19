using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static partial class ProtocolChecks
{
    public static async Task Live()
    {
        var pin = File.ReadAllText(Path.Combine(PluginChecks.Repository, "artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/protocol-live-" + Guid.NewGuid().ToString("N")));
        var accounts = new AccountService(store); var service = new ProtocolMailService(store);
        var bob = await accounts.Save(new(null, "Bob", "pop3", "bob@localhost"));
        var endpoint = new MailEndpoint("127.0.0.1", 3993, "alice@localhost", CertificateSha256: pin);
        var importer = new OutlookAccountImport(accounts);
        var alice = await importer.Import(new(null, "Alice", "imap-smtp", endpoint.User),
            new(endpoint, endpoint with { Port = 3465 }), null, default);
        var settings = await service.Settings(alice.Id);
        await service.Save(alice.Id, settings, "local-alice", "local-alice");
        await service.Save(bob.Id, new(endpoint with { Port = 3995, User = "bob@localhost" }), "local-bob", null);
        var run = "live-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 25; i++)
        {
            var result = await service.Send(alice.Id, new("alice@localhost", "", "bob@localhost", $"{run}-{i:00}-中文", "真实 SMTP 正文 " + i));
            MailChecks.Check(result.State == "sent" && result.Accepted.Count == 2, "SMTP envelope was not accepted");
        }
        using (var client = new ImapClient())
        {
            client.ServerCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == pin;
            await client.ConnectAsync("127.0.0.1", 3993, SecureSocketOptions.SslOnConnect); await client.AuthenticateAsync("alice@localhost", "local-alice");
            var root = client.GetFolder(client.PersonalNamespaces[0]); await root.CreateAsync("中文目录-" + run, true);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly); var latest = await client.Inbox.GetMessageAsync(client.Inbox.Count - 1);
            MailChecks.Check(latest.Subject?.Contains(run) == true && latest.TextBody?.Contains("真实 SMTP") == true, "SMTP body was changed");
            MailChecks.Check(latest.Bcc.Count == 0 && !latest.Headers.Contains(HeaderId.Bcc), "Bcc leaked into MIME");
            await client.DisconnectAsync(true);
        }
        var folders = await service.Folders(alice.Id); var inbox = folders.Single(f => f.Role == "inbox");
        MailChecks.Check(folders.Any(f => f.Name.Contains("中文目录")), "Non-English folder missing");
        using var graph = new GraphMailService(store);
        await MailboxProviderChecks.Probe(BuiltinMailboxAdapters.Create(store,graph,service).Single(p => p.Type.Id == alice.Provider),alice);
        var first = await service.Summaries(alice.Id, inbox.Path, 0, 20); var second = await service.Summaries(alice.Id, inbox.Path, 20, 20);
        MailChecks.Check(first.Items.Count == 20 && first.HasMore && second.Items.Count >= 5 && !first.Items.Select(m => m.Uid).Intersect(second.Items.Select(m => m.Uid)).Any(), "IMAP pagination overlap");
        MailChecks.Check(first.Items.All(m => !m.IsRead), "Read-only IMAP changed read flags");
        await service.Save(alice.Id, settings with { Incoming = endpoint with { CertificateSha256 = "" } }, null, null);
        await Reject(() => service.Folders(alice.Id), "TLS");
        await service.Save(alice.Id, settings, "incorrect-test-password", null);
        await Reject(() => service.Folders(alice.Id), "认证");
        await service.Save(alice.Id, settings, "local-alice", null);
        MailChecks.Check((await service.Folders(alice.Id)).Count > 0, "Authentication recovery failed");
        MailChecks.Check(!(await accounts.List()).Single(a => a.Id == alice.Id).AuthenticationState.Contains("失败"), "Successful reconnection left a stale authentication failure");
        await service.Save(alice.Id, new(endpoint with { Port = 3143, Tls = MailTls.StartTls }, endpoint with { Port = 3025, Tls = MailTls.StartTls }), null, null);
        // GreenMail's IMAP listener does not advertise STARTTLS: verify mandatory TLS never silently falls back.
        await Reject(() => service.Folders(alice.Id), "不会降级");
        foreach (var kind in new[] { MailProxy.Http, MailProxy.Socks5 })
        {
            await using var proxy = new ProtocolProxy(kind == MailProxy.Socks5);
            await service.Save(alice.Id, settings with { Incoming = endpoint with { Proxy = kind, ProxyHost = "127.0.0.1", ProxyPort = proxy.Port } }, null, null);
            MailChecks.Check((await service.Folders(alice.Id)).Count > 0, "Proxy connection failed: " + kind);
        }
        await service.Save(alice.Id, settings, null, null);
        var pop = await service.ReceivePop(bob.Id, false, 10);
        MailChecks.Check(pop.Imported == 10 && pop.HasMore, "POP first batch incomplete");
        var reopened = new ProtocolMailService(new SqliteMailStore(Path.GetDirectoryName(store.DatabasePath)!));
        var rest = await reopened.ReceivePop(bob.Id, false, 100);
        MailChecks.Check(rest.Existing >= 10 && rest.Imported >= 15, "POP restart did not resume");
        var again = await reopened.ReceivePop(bob.Id, false);
        MailChecks.Check(again.Imported == 0 && again.Existing >= 25, "POP restart duplicate import");
        var folder = (await store.Folders()).Single(f => f.AccountId == bob.Id);
        var list = await store.List(folder.Id, 0, 200); var raw = await store.Read(list.Items[0].Id);
        MailChecks.Check((await new MyMailBox.Mail.MimeReader().Parse(raw)).Text.Contains("真实 SMTP"), "POP raw MIME lost");
        var provider = new ProtocolReceiveProvider(store, reopened);
        var unified = new UnifiedInboxService(store, accounts, provider);
        await provider.Receive(alice, 20, default);
        var unifiedFirst = await unified.Inbox(null, null, 5);
        var unifiedNext = await unified.Inbox(null, unifiedFirst.Messages[^1].Position, 5);
        MailChecks.Check(unifiedFirst.HasMore && !unifiedNext.Messages.Intersect(unifiedFirst.Messages).Any(), "Unified pages overlap");
        foreach (var identity in new[] { alice, bob })
        {
            var filtered = await unified.Inbox(identity.Id, null, 5);
            MailChecks.Check(filtered.Messages.Count == 5 && filtered.Messages.All(m => m.Account == identity.Id), "Real protocol account filter failed");
            MailChecks.Check((await unified.Open(identity.Id, filtered.Messages[0].Position.Key)).Contains("真实 SMTP"), "Real unified inbox cannot read mail");
        }
        var removed = await reopened.ReceivePop(bob.Id, true);
        MailChecks.Check(removed.Imported == 0 && removed.DeleteRequested >= 25, "POP deletion not applied to verified persisted mail");
        MailChecks.Check((await reopened.ReceivePop(bob.Id, false)).Existing == 0 && (await store.List(folder.Id, 0, 200)).Items.Count >= 25, "POP source deletion damaged local copy");
        await service.Save(bob.Id, new(endpoint with { Port = 3110, Tls = MailTls.StartTls, User = "bob@localhost" }), null, null);
        await Reject(() => service.ReceivePop(bob.Id, false), "不会降级");
        Console.WriteLine("PASS GreenMail: TLS SMTP To/Bcc/body, Unicode folders, IMAP paging/read-only, certificate/auth rejection+recovery, POP batches/reopen/dedupe/delete preserve local MIME");
    }
}
