using System.Text;
using Microsoft.Data.Sqlite;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;
using MimeReader = MyMailBox.Mail.MimeReader;

internal static class MailChecks
{
    public static byte[] Sample(string subject = "本地邮件") => Encoding.UTF8.GetBytes($"From: Sender <sender@example.test>\r\nTo: reader@example.test\r\nSubject: {subject}\r\nDate: Tue, 17 Nov 2026 08:00:00 +0800\r\nMessage-Id: <same@example.test>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n重启之后仍可阅读。\r\n");
    public static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-mail-" + Guid.NewGuid().ToString("N"));
        var parser = new MimeReader(); var store = new SqliteMailStore(directory);
        var raw = Sample(); var parsed = await parser.Parse(raw);
        var id = await Exercise(store, parser);
        var reopened = new SqliteMailStore(directory);
        Check((await reopened.Read(id)).SequenceEqual(raw), "Raw MIME changed after reopen");
        var second = await store.Import(SqliteMailStore.Inbox, raw, parsed);
        Check(second != id, "Message-ID was used as unique identity");
        for (var i = 0; i < 53; i++) await store.Import(SqliteMailStore.Inbox, raw, parsed);
        var first = await store.List(SqliteMailStore.Inbox, 0, 50);
        var last = await store.List(SqliteMailStore.Inbox, 50, 50);
        Check(first.Items.Count == 50 && first.HasMore && last.Items.Count == 5 && !last.HasMore, "Paging failed");
        Check(!first.Items.Select(m => m.Id).Intersect(last.Items.Select(m => m.Id)).Any(), "Pages overlap");
        try { await store.Import(Guid.NewGuid(), raw, parsed); throw new Exception("Missing folder accepted"); } catch (SqliteException) { }
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "CREATE TRIGGER fail_write BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT,'injected write failure'); END"; command.ExecuteNonQuery();
            try { await store.Import(SqliteMailStore.Inbox, raw, parsed); throw new Exception("Failure not injected"); } catch (SqliteException) { }
        }
        Check((await store.List(SqliteMailStore.Inbox, 0, 100)).Items.Count == 55, "Failed insert visible");
        try { await parser.Parse(Encoding.UTF8.GetBytes("not an email")); throw new Exception("Invalid EML accepted"); } catch (Exception error) when (error is FormatException or InvalidDataException) { }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await store.Import(SqliteMailStore.Inbox, raw, parsed, canceled.Token); throw new Exception("Canceled import accepted"); } catch (OperationCanceledException) { }
        await Exercise(new MemoryStore(), parser);
        var contributions = new Contributions(); await using var runtime = new PluginRuntime(contributions);
        var catalog = DefaultProfile.Catalog(directory, new MemoryStore(), parser);
        await runtime.Start(catalog.Find(new("mail-test", "local-mail", "1.0.0")), new PluginOptions { InstanceId = "mail-test" });
        var reader = contributions.Pages.Single(p => p.Reader is not null).Reader!;
        await Exercise(reader.Store, reader.Parser);
        await runtime.Stop("mail-test");
        Check(contributions.Pages.Count == 0, "Mail plugin page leaked");
        var rejected = false;
        try { await reader.Store.Folders(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Unloaded store still accepts work");
        Console.WriteLine("PASS mail: atomic MIME + metadata, reopen, paging, failure rollback, cancellation, second store, plugin lifetime");
    }

    private static async Task<Guid> Exercise(IMailStore store, IMimeReader parser)
    {
        var folders = await store.Folders(); Check(folders.Count > 0, "No folders");
        var raw = Sample(); var parsed = await parser.Parse(raw);
        var id = await store.Import(folders[0].Id, raw, parsed);
        var page = await store.List(folders[0].Id, 0, 50);
        Check(page.Items.Any(m => m.Id == id && m.Subject == "本地邮件"), "Metadata not readable");
        Check((await store.Read(id)).SequenceEqual(raw), "Raw differs");
        Check((await parser.Parse(await store.Read(id))).Text.Contains("重启之后"), "Body not readable");
        return id;
    }

    private sealed class MemoryStore : IMailStore
    {
        private readonly List<(MailSummary Summary, byte[] Raw)> _mail = [];
        private readonly MailFolder _folder = new(Guid.NewGuid(), Guid.NewGuid(), "测试", "第二实现 / 测试");
        public Task<IReadOnlyList<MailFolder>> Folders(CancellationToken cancellation = default) => Task.FromResult<IReadOnlyList<MailFolder>>([_folder]);
        public Task<MailPageResult> List(Guid folderId, int offset, int limit, CancellationToken cancellation = default) => Task.FromResult(new MailPageResult(_mail.Where(m => m.Summary.FolderId == folderId).Skip(offset).Take(limit).Select(m => m.Summary).ToArray(), offset, _mail.Count > offset + limit));
        public Task<Guid> Import(Guid folderId, byte[] raw, ParsedMail metadata, CancellationToken cancellation = default)
        {
            var id = Guid.NewGuid(); _mail.Add((new(id, folderId, metadata.Subject, metadata.From, metadata.To, metadata.Date, raw.Length), raw.ToArray())); return Task.FromResult(id);
        }
        public Task<byte[]> Read(Guid messageId, CancellationToken cancellation = default) => Task.FromResult(_mail.Single(m => m.Summary.Id == messageId).Raw.ToArray());
    }
}
