using System.IO.Compression;
using System.Text.Json;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class ArchiveChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-archive-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(Path.Combine(directory, "source")); var accounts = new AccountService(store);
        var local = await accounts.Save(new(null, "项目归档", "local", Alias: "project", Tags: ["重要", "项目"]));
        var folder = (await store.Folders()).Single(f => f.AccountId == local.Id);
        var raw = MimeChecks.Multipart(); var parsed = await new MimeReader().Parse(raw); var id = await store.Import(folder.Id, raw, parsed);
        await store.Import(SqliteMailStore.Inbox, MailChecks.Sample("不在范围"), await new MimeReader().Parse(MailChecks.Sample("不在范围")));
        var archive = new MailArchive(store); var file = Path.Combine(directory, "mail.mmbarchive"); await archive.Export([folder.Id], file);
        using (var zip = ZipFile.OpenRead(file))
        {
            using var manifestStream = zip.GetEntry("manifest.json")!.Open(); var manifest = JsonSerializer.Deserialize<MailArchiveManifest>(manifestStream)!;
            MailChecks.Check(manifest.Messages.Length == 1 && manifest.Accounts.All(a => a.CredentialReference is null), "Archive scope/credentials wrong");
            MailChecks.Check(zip.Entries.Count == 2, "Workspace settings leaked into mail archive");
        }
        var target = new SqliteMailStore(Path.Combine(directory, "target")); var importer = new MailArchive(target);
        var first = await importer.Import(file);
        MailChecks.Check(first.Messages == 1 && first.AccountMapping[local.Id] == local.Id && first.MessageMapping[id] == id, "No-conflict IDs changed");
        MailChecks.Check((await target.Read(id)).SequenceEqual(raw), "Archive raw fidelity lost");
        var imported = (await new AccountService(target).List()).Single(a => a.Id == local.Id);
        MailChecks.Check(imported.Tags.SequenceEqual(local.Tags) && imported.Provider == "local", "Tags/local ownership lost");
        var again = await importer.Import(file);
        MailChecks.Check(again.AccountMapping[local.Id] != local.Id && again.FolderMapping[folder.Id] != folder.Id && again.MessageMapping[id] != id, "Collision not remapped");
        MailChecks.Check((await new AccountService(target).List()).Single(a => a.Id == again.AccountMapping[local.Id]).Alias != local.Alias, "Alias conflict ignored");
        MailChecks.Check((await target.Read(id)).SequenceEqual(raw) && (await target.Read(again.MessageMapping[id])).SequenceEqual(raw), "Collision overwrote original");
        var bad = Path.Combine(directory, "bad.mmbarchive"); File.Copy(file, bad);
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.Single(e => e.FullName.EndsWith(".eml")); var name = entry.FullName; var length = entry.Length; entry.Delete(); using var output = zip.CreateEntry(name).Open(); output.Write(new byte[length]);
        }
        var before = await new StorageMaintenance(target, new MimeReader()).Inspect(); var count = (await new AccountService(target).List()).Count;
        var rejected = false; try { await importer.Import(bad); } catch (InvalidDataException) { rejected = true; }
        MailChecks.Check(rejected && (await new StorageMaintenance(target, new MimeReader()).Inspect()).Messages == before.Messages && (await new AccountService(target).List()).Count == count, "Bad archive left partial import");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await importer.Import(file, cancellation.Token); throw new Exception("Canceled archive imported"); } catch (OperationCanceledException) { }
        Console.WriteLine("PASS mail archive: selected scope, manifest/checksums/tags, no secrets/settings, preserved IDs, collision maps/aliases, byte fidelity, corrupt import rollback, cancellation");
    }
}
