using System.IO.Compression;
using MyMailBox.Builtins;
using MyMailBox.Mail;

internal static class BackupChecks
{
    public static async Task Run()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "MyMailBox-backup-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(Path.Combine(workspace, "mail")); var accounts = new AccountService(store); var parser = new MimeReader();
        var account = await accounts.Save(new(null, "工作", "smtp", "work@example.test", "work"), "private-test-password");
        var raw = MimeChecks.Multipart(); var parsed = await parser.Parse(raw); var id = await store.Import(SqliteMailStore.Inbox, raw, parsed);
        DefaultProfile.Ensure(Path.Combine(workspace, "profiles/default"));
        Directory.CreateDirectory(Path.Combine(workspace, "plugin-data/example")); await File.WriteAllTextAsync(Path.Combine(workspace, "plugin-data/example/state.json"), "{\"counter\":3}");
        var backup = new WorkspaceBackup(workspace, store); var file = Path.Combine(workspace, "snapshot.mmbbackup");
        var writes = Task.Run(async () => { for (var i = 0; i < 20; i++) { await store.Import(SqliteMailStore.Inbox, raw, parsed); await Task.Delay(10); } });
        await backup.Create(file); await writes;
        var restored = workspace + "-restored"; await backup.Restore(file, restored);
        var restoredStore = new SqliteMailStore(Path.Combine(restored, "mail"));
        MailChecks.Check((await restoredStore.Read(id)).SequenceEqual(raw), "Backup raw mismatch");
        var report = await new StorageMaintenance(restoredStore, parser).Inspect();
        MailChecks.Check(report.Problems.Length == 0 && report.Messages is >= 1 and <= 21, "Concurrent snapshot inconsistent");
        var restoredAccount = (await new AccountService(restoredStore).List()).Single(a => a.Id == account.Id);
        MailChecks.Check(restoredAccount.Alias == "work" && restoredAccount.CredentialReference is null && restoredAccount.AuthenticationState.Contains("重新授权"), "Account reference/authorization restore failed");
        using (var db = restoredStore.Open()) { using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM credentials"; MailChecks.Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "Backup retained credentials"); }
        MailChecks.Check(File.ReadAllText(Path.Combine(restored, "plugin-data/example/state.json")).Contains("counter"), "Plugin data omitted");
        MailChecks.Check(File.Exists(Path.Combine(restored, "profiles/default/plugins.json")), "Profile omitted");
        await Reject(() => backup.Create(file), "存在"); await Reject(() => backup.Restore(file, restored), "新目录");
        var corrupt = Path.Combine(workspace, "corrupt.mmbbackup"); File.Copy(file, corrupt);
        using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("mail/mail.db")!; entry.Delete(); var changed = zip.CreateEntry("mail/mail.db"); using var output = changed.Open(); output.Write([1, 2, 3]);
        }
        await Reject(() => backup.Restore(corrupt, workspace + "-bad"), "长度");
        MailChecks.Check(!Directory.Exists(workspace + "-bad") && File.Exists(file), "Failed restore exposed partial target");
        var duplicate = Path.Combine(workspace, "duplicate.mmbbackup"); File.Copy(file, duplicate);
        using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Update)) zip.CreateEntry("../escape.txt");
        await Reject(() => backup.Restore(duplicate, workspace + "-escape"), "未声明");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await backup.Create(Path.Combine(workspace, "canceled.mmbbackup"), canceled.Token); throw new Exception("Backup cancellation ignored"); } catch (OperationCanceledException) { }
        MailChecks.Check(!File.Exists(Path.Combine(workspace, "canceled.mmbbackup")), "Canceled backup published");
        await new StorageMaintenance(restoredStore, parser).Rebuild();
        Console.WriteLine("PASS backup: live writes + SQLite online snapshot, original MIME/IDs/settings/plugin data, credential exclusion, conflicts, corruption, undeclared paths, cancel, rebuild");
    }
    private static async Task Reject(Func<Task<string>> action, string expected)
    {
        try { await action(); } catch (Exception error) when ((error is IOException or InvalidDataException) && error.Message.Contains(expected)) { return; }
        throw new Exception("Expected backup failure: " + expected);
    }
}
