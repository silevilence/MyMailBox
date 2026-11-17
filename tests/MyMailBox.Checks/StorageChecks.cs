using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MyMailBox.Mail;

internal static class StorageChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-storage-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(directory); var parser = new MimeReader(); var maintenance = new StorageMaintenance(store, parser);
        var raw = MailChecks.Sample(); var id = await store.Import(SqliteMailStore.Inbox, raw, await parser.Parse(raw));
        for (var stage = 0; stage < 3; stage++)
        {
            var ready = Path.Combine(directory, "ready-" + stage);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(StorageChecks).Assembly.Location); start.ArgumentList.Add("crash-writer"); start.ArgumentList.Add(directory); start.ArgumentList.Add(stage.ToString()); start.ArgumentList.Add(ready);
            using var child = Process.Start(start)!;
            try
            {
                for (var attempt = 0; !File.Exists(ready) && attempt < 100; attempt++) { if (child.HasExited) throw new Exception("Crash writer exited prematurely"); await Task.Delay(50); }
                if (!File.Exists(ready)) throw new Exception("Crash writer did not signal");
                child.Kill(); await child.WaitForExitAsync();
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            var reopened = new SqliteMailStore(directory);
            MailChecks.Check((await reopened.List(SqliteMailStore.Inbox, 0, 50)).Items.Count == 1 && (await reopened.Read(id)).SequenceEqual(raw), "Crash left partial message or damaged completed mail");
        }
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "INSERT INTO disposable_cache VALUES('preview',zeroblob(2048)); UPDATE messages SET subject='stale'"; command.ExecuteNonQuery();
        }
        MailChecks.Check((await maintenance.Inspect()).CacheBytes == 2048, "Cache not classified");
        MailChecks.Check(await maintenance.ClearCache() == 2048 && (await store.Read(id)).SequenceEqual(raw), "Cache cleanup removed archive");
        MailChecks.Check(await maintenance.Rebuild() == 1 && (await store.List(SqliteMailStore.Inbox, 0, 50)).Items[0].Subject == "本地邮件", "Rebuild failed");
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA page_count"; var pages = Convert.ToInt32(command.ExecuteScalar());
            command.CommandText = "PRAGMA max_page_count=" + pages; command.ExecuteScalar();
            using var transaction = db.BeginTransaction(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO messages(id,folder_id,subject,sender,recipient,date,size,raw,sha256) SELECT 'disk-full',folder_id,subject,sender,recipient,date,5000000,zeroblob(5000000),'incomplete' FROM messages LIMIT 1";
            var full = false; try { command.ExecuteNonQuery(); } catch (SqliteException error) when (error.SqliteErrorCode == 13) { full = true; }
            MailChecks.Check(full, "Actual SQLite disk capacity failure not reached");
        }
        MailChecks.Check((await store.List(SqliteMailStore.Inbox, 0, 50)).Items.Count == 1 && (await store.Read(id)).SequenceEqual(raw), "Disk-full lost source");
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "UPDATE messages SET sha256='damaged'"; command.ExecuteNonQuery();
        }
        MailChecks.Check((await maintenance.Inspect()).Problems.Any(p => p.Contains("校验")), "Damage not diagnosed");
        var rejected = false; try { await maintenance.Rebuild(); } catch (InvalidDataException) { rejected = true; }
        MailChecks.Check(rejected && (await store.Read(id)).SequenceEqual(raw), "Rebuild silently accepted corruption");
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=OFF; UPDATE messages SET folder_id='orphan'"; command.ExecuteNonQuery();
        }
        MailChecks.Check((await maintenance.Inspect()).Problems.Any(p => p.Contains("孤立")), "Orphan not diagnosed");
        using (var db = store.Open()) { using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery(); }
        rejected = false; try { _ = new SqliteMailStore(directory); } catch (InvalidDataException) { rejected = true; }
        MailChecks.Check(rejected, "Future schema opened for writes");
        await Migration();
        Console.WriteLine("PASS storage: process-kill at raw/metadata/index phases, SQLITE_FULL, cache/archive separation, checksum/orphan diagnostics, rebuild, v0 migration, future-version refusal");
    }

    public static void CrashWriter(string directory, int stage, string ready)
    {
        var store = new SqliteMailStore(directory); using var db = store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO messages(id,folder_id,subject,sender,recipient,date,size,raw,sha256) SELECT 'unfinished',folder_id,subject,sender,recipient,date,size,raw,sha256 FROM messages LIMIT 1"; command.ExecuteNonQuery();
        if (stage >= 1) { command.CommandText = "UPDATE messages SET subject='uncommitted' WHERE id='unfinished'"; command.ExecuteNonQuery(); }
        if (stage >= 2) { command.CommandText = "REINDEX messages_folder"; command.ExecuteNonQuery(); }
        File.WriteAllText(ready, "uncommitted"); Thread.Sleep(Timeout.Infinite);
    }

    private static async Task Migration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-v0-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "mail.db")))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE folders(id TEXT PRIMARY KEY,account_id TEXT NOT NULL,name TEXT NOT NULL,path TEXT NOT NULL,role TEXT NOT NULL,parent_id TEXT); CREATE TABLE messages(id TEXT PRIMARY KEY,folder_id TEXT NOT NULL REFERENCES folders(id),subject TEXT NOT NULL,sender TEXT NOT NULL,recipient TEXT NOT NULL,date TEXT NOT NULL,size INTEGER NOT NULL,is_read INTEGER NOT NULL DEFAULT 0,raw BLOB NOT NULL);"; command.ExecuteNonQuery();
        }
        var migrated = new SqliteMailStore(directory); var raw = MailChecks.Sample(); var id = await migrated.Import(SqliteMailStore.Inbox, raw, await new MimeReader().Parse(raw));
        MailChecks.Check((await new StorageMaintenance(migrated, new MimeReader()).Inspect()).Problems.Length == 0 && (await migrated.Read(id)).SequenceEqual(raw), "v0 migration failed");
    }
}
