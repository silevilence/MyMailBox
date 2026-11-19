using System.Security.Cryptography;
using System.Diagnostics;
using MyMailBox.Mail;
using MyMailBox.Contracts;

internal static class PstImportChecks
{
    internal static string Fixtures => Path.Combine(PluginChecks.Repository, "artifacts/outlook-acceptance/final");
    public static async Task Run()
    {
        var path = Path.Combine(Fixtures, "read-ansi.pst");
        var store = new SqliteMailStore(Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "pst"));
        var mime = new MimeReader();
        var local = new LocalMailboxService(store, new AccountService(store), mime);
        var target = await local.Create(new(null, "PST 验收", "local"));
        var service = new PstImportService(store, local, mime);
        var preview = await service.Preview(path, 936, default);
        Require(preview.Folders.Any(f => f.Path.EndsWith("测试邮件 / 嵌套目录") && f.Items == 4), "Folder preview");
        Require(!(await local.Folders(target.Id)).Any(f => f.Name == "嵌套目录"), "Preview wrote folders");
        using var cancel = new CancellationTokenSource();
        try
        {
            await service.Import(preview, target.Id, new OnItem(_ => cancel.Cancel()), cancel.Token);
            throw new Exception("Import ignored cancellation");
        }
        catch (OperationCanceledException) { }
        var folders = await local.Folders(target.Id);
        var nested = folders.Single(f => f.Name == "嵌套目录");
        Require(folders.Single(f => f.Id == nested.ParentId).Name == "测试邮件", "Lost folder parent");
        var saved = (await store.List(nested.Id, 0, 20)).Items.Single();
        var result = await service.Import(preview, target.Id, null, default);
        Require(result.Counts is { Imported: 3, Duplicates: 1, Failed: 0 }, "Resume did not retain exactly one committed message");
        var items = (await store.List(nested.Id, 0, 20)).Items;
        Require(items.Count == 4 && items.Any(m => m.Id == saved.Id), "Unstable IDs");
        var plain = items.Single(m => m.Subject == "MMB-plain-中文");
        var parsed = await mime.Parse(await store.Read(plain.Id));
        Require(plain.IsRead && items.Count(m => m.IsRead) == 1 && plain.Date.Year == 2026 && parsed.Text.Contains("纯文本正文 Alpha 中文"), "Read metadata or text");
        Require(File.ReadLines(result.Report).Count() == 4, "Missing item report");
        using var source = File.OpenRead(path);
        Require(preview.Fingerprint == Convert.ToHexString(await SHA256.HashDataAsync(source)), "Changed source");
        try { await service.Import(preview with { Fingerprint = "changed" }, target.Id, null, default); throw new Exception("Stale preview accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS PST product import: preview, hierarchy, item metadata, cancel/resume, stable IDs, reports, read-only source");
    }

    internal static void Require(bool value, string condition) { if (!value) throw new Exception(condition); }
    public static async Task CrashWorker(string workspace, Guid target)
    {
        var store = new SqliteMailStore(workspace);
        var mime = new MimeReader();
        var service = new PstImportService(store, new LocalMailboxService(store, new AccountService(store), mime), mime);
        var preview = await service.Preview(Path.Combine(Fixtures, "read-ansi.pst"), 936, default);
        await service.Import(preview, target, new OnItem(_ =>
        {
            File.WriteAllText(Path.Combine(workspace, "committed"), "1");
            Thread.Sleep(Timeout.Infinite);
        }), default);
    }
    public static async Task Recovery()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "pst-restart");
        var store = new SqliteMailStore(directory);
        var mime = new MimeReader();
        var local = new LocalMailboxService(store, new AccountService(store), mime);
        var account = await local.Create(new(null, "中断导入", "local"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { typeof(PstImportChecks).Assembly.Location, "pst-worker", directory, account.Id.ToString() }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(directory, "committed")))
            {
                Require(!process.HasExited, "Importer exited before first commit");
                await Task.Delay(50, timeout.Token);
            }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        var service = new PstImportService(store, local, mime);
        var saved = (await service.Pending(default)).Single();
        Require(saved.Target == account.Id && saved.Committed == 1 && File.Exists(saved.Report), "Lost import session after process kill");
        var preview = await service.Preview(saved.File, saved.CodePage, default);
        Require(preview.Fingerprint == saved.Fingerprint, "Resume source did not match");
        // Fail the second half of a mail+cursor transaction. Reopening must expose neither half.
        using var db = store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE capacity_test(id INTEGER PRIMARY KEY AUTOINCREMENT);
            INSERT INTO capacity_test VALUES(9223372036854775807);
            CREATE TRIGGER reject_checkpoint BEFORE INSERT ON pst_committed BEGIN
                INSERT INTO capacity_test(id) VALUES(NULL);
            END;
            """;
        cmd.ExecuteNonQuery();
        try { await service.Import(preview, account.Id, null, default); throw new Exception("Missed write fault"); }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 13) { } // SQLITE_FULL, induced by an exhausted test rowid sequence.
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        Require(Convert.ToInt32(cmd.ExecuteScalar()) == 1 && (await service.Pending(default)).Single().Committed == 1, "Cursor and mail diverged");
        cmd.CommandText = "DROP TRIGGER reject_checkpoint"; cmd.ExecuteNonQuery();
        var result = await service.Import(preview, account.Id, null, default);
        Require(result.Counts is { Imported: 3, Duplicates: 1, Failed: 0 } && (await service.Pending(default)).Count == 0, "Restart failed");
        Console.WriteLine("PASS PST session: killed importer after commit, reopened persistent source/target, rolled back item+cursor on SQLITE_FULL, resumed remaining nodes");
    }
    private sealed class OnItem(Action<PstProgress> action) : IProgress<PstProgress> { public void Report(PstProgress value) => action(value); }
}
