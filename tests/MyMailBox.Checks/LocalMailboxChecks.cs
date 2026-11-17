using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class LocalMailboxChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-local-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(Path.Combine(directory, "mail")); var accounts = new AccountService(store); var parser = new MimeReader();
        ILocalMailboxService service = new LocalMailboxService(store, accounts, parser); IMailStore visible = new AvailableMailStore(store);
        var first = await service.Create(new(null, "年度归档", "local", Alias: "year", Tags: ["归档"]));
        var second = await service.Create(new(null, "第二归档", "local"));
        var parent = await service.CreateFolder(first.Id, null, "2026"); var folder = await service.CreateFolder(first.Id, parent.Id, "项目");
        await Reject(() => service.CreateFolder(first.Id, parent.Id, "项目"), "同名");
        await Reject(() => service.CreateFolder(second.Id, parent.Id, "跨邮箱"), "另一邮箱");
        await Reject(() => service.CreateFolder(first.Id, null, "../escape"), "斜线");
        var raw = MimeChecks.Multipart(); var id = await service.Archive(folder.Id, raw);
        MailChecks.Check((await visible.Read(id)).SequenceEqual(raw), "Complete MIME/attachment bytes changed");
        await service.SetOpen(first.Id, false, first.Alias);
        MailChecks.Check(!(await visible.Folders()).Any(f => f.AccountId == first.Id), "Closed mailbox still in UI");
        await Reject(async () => { await visible.Read(id); }, "关闭");
        await Reject(async () => { await service.Archive(folder.Id, raw); }, "关闭");
        MailChecks.Check((await store.Read(id)).SequenceEqual(raw), "Close deleted raw MIME");
        var reopenedStore = new SqliteMailStore(Path.Combine(directory, "mail")); var reopened = new LocalMailboxService(reopenedStore, new AccountService(reopenedStore), parser);
        var state = (await reopened.List()).Single(m => m.Id == first.Id);
        MailChecks.Check(!state.Open && state.Messages == 1 && state.Tags.Contains("归档"), "Restart lost metadata/closed state");
        await reopened.SetOpen(first.Id, true, first.Alias);
        MailChecks.Check((await new AvailableMailStore(reopenedStore).Read(id)).SequenceEqual(raw), "Reopen lost stable message reference");
        await accounts.Remove(first.Id);
        MailChecks.Check((await reopened.List()).Single(m => m.Id == first.Id).Removed && (await store.Read(id)).SequenceEqual(raw), "Removal hid retained archive from reopening");
        await accounts.Save(new(second.Id, second.Name, "local", Alias: "year"));
        await Reject(() => reopened.SetOpen(first.Id, true, "year"), "alias");
        await reopened.SetOpen(first.Id, true, "year-restored");
        MailChecks.Check((await reopened.List()).Single(m => m.Id == first.Id) is { Open: true, Removed: false, Messages: 1 }, "Removed mailbox could not be restored");
        await accounts.Save(new(first.Id, "改名归档", "local", Alias: "year-restored", Tags: ["已审"]));
        MailChecks.Check((await visible.Folders()).Single(f => f.Id == folder.Id).Path.StartsWith("改名归档 / "), "Rename retained stale visible account path");
        var integrity = await new StorageMaintenance(store, parser).Inspect(); MailChecks.Check(integrity.Problems.Length == 0, "Local archive integrity failed");
        var backup = await new WorkspaceBackup(directory, store).Create(Path.Combine(directory, "backup.zip"));
        MailChecks.Check(File.Exists(backup) && service.StorageDescription.Contains("v1"), "Version/backup entry missing");
        Console.WriteLine("PASS local mailboxes: create/nested folders, complete MIME, close/reopen/restart, retained removed archive, alias remap, stable references, integrity and backup");
    }
    private static async Task Reject(Func<Task> action, string detail)
    {
        try { await action(); } catch (Exception e) when (e.Message.Contains(detail)) { return; }
        throw new Exception("Expected rejection: " + detail);
    }
}
