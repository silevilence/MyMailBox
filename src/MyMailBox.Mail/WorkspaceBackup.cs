using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class WorkspaceBackup(string workspace, SqliteMailStore store) : IWorkspaceBackup
{
    public Task<string> Create(string destination, CancellationToken cancellation = default) => Task.Run(async () =>
    {
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("备份文件已存在，请选择新文件名；不会覆盖已有备份。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        var snapshot = temporary + ".db";
        try
        {
            var before = await ConfigurationSnapshot(cancellation);
            using (var source = store.Open())
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString()))
            {
                target.Open();
                var waiting = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try { source.BackupDatabase(target); break; }
                    catch (SqliteException e) when ((e.SqliteErrorCode & 0xff) is 5 or 6 && waiting.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        // Online backup can return BUSY_RECOVERY while another connection opens the WAL.
                        await Task.Delay(75, cancellation);
                    }
                }
                using var command = target.CreateCommand();
                // The backup is portable. Credential references must never claim authorization on another machine.
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='accounts'";
                if (Convert.ToInt32(command.ExecuteScalar()) > 0)
                {
                    command.CommandText = "DELETE FROM credentials; UPDATE accounts SET credential_ref=NULL,auth_state=CASE WHEN provider='local' THEN '无需登录' ELSE '需要重新授权' END;"; command.ExecuteNonQuery();
                }
                command.CommandText = "DELETE FROM disposable_cache; PRAGMA journal_mode=DELETE; VACUUM;"; command.ExecuteNonQuery();
            }
            cancellation.ThrowIfCancellationRequested();
            var entries = new List<BackupFile>();
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                await Add(archive, snapshot, "mail/mail.db", entries, cancellation);
                foreach (var file in before) await Add(archive, Path.Combine(workspace, file.Path.Replace('/', Path.DirectorySeparatorChar)), file.Path, entries, cancellation);
                var after = await ConfigurationSnapshot(cancellation);
                if (!before.SequenceEqual(after) || !entries.Skip(1).SequenceEqual(before)) throw new IOException("备份期间配置或插件文件发生变化，请稍后重试。邮箱写入不受影响。");
                var manifest = archive.CreateEntry("manifest.json");
                await using var output = manifest.Open(); await JsonSerializer.SerializeAsync(output, new WorkspaceBackupManifest(1, DateTimeOffset.UtcNow, entries.ToArray()), cancellationToken: cancellation);
            }
            cancellation.ThrowIfCancellationRequested(); File.Move(temporary, destination); return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(snapshot)) File.Delete(snapshot);
        }
    }, cancellation);

    public Task<string> Restore(string source, string newWorkspace, CancellationToken cancellation = default) => Task.Run(async () =>
    {
        source = Path.GetFullPath(source); newWorkspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newWorkspace));
        if (Directory.Exists(newWorkspace) || File.Exists(newWorkspace)) throw new IOException("恢复目标必须是尚不存在的新目录。现有工作空间不会被覆盖。");
        using var archive = ZipFile.OpenRead(source);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少备份清单。");
        if (manifestEntry.Length > 4 * 1024 * 1024) throw new InvalidDataException("备份清单超过大小限制。");
        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<WorkspaceBackupManifest>(manifestStream, cancellationToken: cancellation)
            ?? throw new InvalidDataException("备份清单为空。");
        if (manifest.Schema != 1 || manifest.CredentialsIncluded || manifest.Files is null || !manifest.Files.Any(f => f.Path == "mail/mail.db")) throw new InvalidDataException("备份版本或内容不受支持。");
        if (manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count) throw new InvalidDataException("备份包含重复路径。");
        if (archive.Entries.Count != manifest.Files.Length + 1 || archive.Entries.Any(e => e.FullName != "manifest.json" && !manifest.Files.Any(f => f.Path == e.FullName))) throw new InvalidDataException("备份存在未声明内容。");
        var staging = newWorkspace + ".restoring-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in manifest.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (file.Size < 0 || file.Path.Contains('\\') || file.Path.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':'))) throw new InvalidDataException("备份包含危险路径。");
                var path = Path.GetFullPath(Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份路径越界。");
                var entry = archive.GetEntry(file.Path) ?? throw new InvalidDataException("备份缺少文件：" + file.Path);
                if (entry.Length != file.Size) throw new InvalidDataException("文件长度不一致：" + file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var input = entry.Open()) await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)) await input.CopyToAsync(output, cancellation);
                if (await Hash(path, cancellation) != file.Sha256) throw new InvalidDataException("文件校验失败：" + file.Path);
            }
            var restoredStore = new SqliteMailStore(Path.Combine(staging, "mail"));
            var report = await new StorageMaintenance(restoredStore, new MimeReader()).Inspect(cancellation);
            if (report.Problems.Length > 0) throw new InvalidDataException("备份数据库完整性未通过：" + string.Join("；", report.Problems));
            cancellation.ThrowIfCancellationRequested(); Directory.Move(staging, newWorkspace); return newWorkspace;
        }
        catch (Exception error)
        {
            // Keep diagnostic staging out of the active workspace; the original backup and source are untouched.
            await File.WriteAllTextAsync(Path.Combine(staging, "restore-failed.txt"), "恢复未完成，未切换工作空间。原备份仍可重试。\n" + error.Message, CancellationToken.None);
            throw;
        }
    }, cancellation);

    private async Task<BackupFile[]> ConfigurationSnapshot(CancellationToken cancellation)
    {
        var result = new List<BackupFile>();
        foreach (var root in new[] { "profiles", "plugins", "plugin-data" })
        {
            var directory = Path.Combine(workspace, root); if (!Directory.Exists(directory)) continue;
            foreach (var file in EnumerateSafe(directory).Order(StringComparer.Ordinal))
            {
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("备份不跟随重解析文件：" + info.Name);
                result.Add(new(Path.GetRelativePath(workspace, file).Replace('\\', '/'), info.Length, await Hash(file, cancellation)));
            }
        }
        return result.ToArray();
    }
    private static IEnumerable<string> EnumerateSafe(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("备份不跟随重解析目录：" + Path.GetFileName(directory));
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("备份不跟随重解析路径：" + Path.GetFileName(entry));
            if (attributes.HasFlag(FileAttributes.Directory)) { foreach (var file in EnumerateSafe(entry)) yield return file; }
            else yield return entry;
        }
    }
    private static async Task Add(ZipArchive archive, string path, string name, List<BackupFile> entries, CancellationToken cancellation)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; int count; long size = 0;
        while ((count = await input.ReadAsync(buffer, cancellation)) > 0) { hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), cancellation); size += count; }
        entries.Add(new(name, size, Convert.ToHexString(hash.GetHashAndReset())));
    }
    private static async Task<string> Hash(string path, CancellationToken cancellation)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
    }
}
