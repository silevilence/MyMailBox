using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class SelectionChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-selectors-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(directory); var accounts = new AccountService(store); var service = new SelectionService(store, accounts);
        var a = await accounts.Save(new(null, "工作", "smtp", "work@example.test", Tags: ["project"]));
        var b = await accounts.Save(new(null, "归档", "local", Tags: ["project"]));
        var c = await accounts.Save(new(null, "停用", "smtp", "off@example.test", Enabled: false));
        var senders = await service.Query(new(SelectionKind.Identity));
        MailChecks.Check(senders.Single(x => x.Reference.AccountId == a.Id).Available, "Send identity missing");
        MailChecks.Check(!senders.Single(x => x.Reference.AccountId == b.Id).Available && senders.Single(x => x.Reference.AccountId == b.Id).Reason.Contains("发送"), "Local identity not disabled/explained");
        MailChecks.Check(!senders.Single(x => x.Reference.AccountId == c.Id).Available, "Disabled account selectable");
        MailChecks.Check((await service.Query(new(SelectionKind.Account, Search: "work@example"))).Single().Reference.AccountId == a.Id, "Address search failed");
        var reference = new SelectionReference(SelectionKind.Account, a.Id);
        var fixedSet = new SavedSelection(false, [], [reference], SelectionKind.Account, AccountCapabilities.None);
        await service.Save("organize.account", fixedSet);
        var dynamicSet = new SavedSelection(true, ["project"], [], SelectionKind.Account, AccountCapabilities.None);
        await service.Save("dynamic", dynamicSet);
        await accounts.Save(new(a.Id, "改名", "smtp", a.Address, Tags: []));
        var d = await accounts.Save(new(null, "新增", "local", Tags: ["project"]));
        MailChecks.Check((await service.Resolve(fixedSet)).Choices.Single().Name == "改名", "Rename broke fixed reference");
        var dynamicResult = await service.Resolve(dynamicSet);
        MailChecks.Check(dynamicResult.Choices.Select(x => x.Reference.AccountId).Order().SequenceEqual(new[] { b.Id, d.Id }.Order()), "Dynamic tags frozen or stale");
        MailChecks.Check((await service.Query(new(SelectionKind.Account, RecentOnly: true))).Any(x => x.Reference.AccountId == a.Id), "Recent selection lost");
        var folder = (await store.Folders()).Single(f => f.AccountId == b.Id);
        var folderSet = new SavedSelection(false, [], [new(SelectionKind.Folder, b.Id, folder.Id)], SelectionKind.Folder, AccountCapabilities.Import);
        await service.Save("organize.folder", folderSet);
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "UPDATE folders SET name='重命名目录',path='归档 / 重命名目录' WHERE id=$id"; command.Parameters.AddWithValue("$id", folder.Id.ToString()); command.ExecuteNonQuery();
        }
        MailChecks.Check((await service.Resolve(folderSet)).Choices.Single().Name == "重命名目录", "Folder rename broke reference");
        var reopened = new SelectionService(new SqliteMailStore(directory), new AccountService(store));
        var restored = await reopened.Load("organize.folder");
        MailChecks.Check(restored is not null && (await reopened.Resolve(restored)).Choices.Single().Reference.FolderId == folder.Id, "Persistence lost");
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "DELETE FROM folders WHERE id=$id"; command.Parameters.AddWithValue("$id", folder.Id.ToString()); command.ExecuteNonQuery();
        }
        MailChecks.Check((await service.Resolve(folderSet)).Missing.Length == 1, "Deleted folder is not invalid");
        await accounts.Remove(a.Id);
        MailChecks.Check((await service.Resolve(fixedSet)).Missing.Length == 1, "Deleted account is not invalid");
        var rejected = false;
        try { await service.Save("invalid", new(false, [], [new(SelectionKind.Identity, b.Id)], SelectionKind.Identity, AccountCapabilities.Send)); } catch (InvalidDataException) { rejected = true; }
        MailChecks.Check(rejected, "Unavailable identity accepted");
        Console.WriteLine("PASS selectors: capability reasons, search, tags, recent, fixed/dynamic semantics, stable rename, deleted references, restart");
    }
}
