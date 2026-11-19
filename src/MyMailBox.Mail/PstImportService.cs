using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using MyMailBox.Pst;

namespace MyMailBox.Mail;

public sealed class PstImportService(SqliteMailStore store, ILocalMailboxService mailboxes, IMimeReader parser) : IPstImport
{
    public async Task<IReadOnlyList<LocalMailboxInfo>> Targets(CancellationToken token) =>
        (await mailboxes.List(token)).Where(mailbox => mailbox.Open).ToArray();

    public async Task<PstPreview> Preview(string file, int? ansiCodePage, CancellationToken token)
    {
        var path = Path.GetFullPath(file);
        using var source = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
        var paths = new Dictionary<uint, string>();
        var folders = new List<PstFolderPreview>();
        await foreach (var entry in Read("preview", path, ansiCodePage, token))
        {
            var name = Name(entry, path);
            var location = entry.Parent is uint parent ? paths[parent] + " / " + name : name;
            paths.Add(entry.Node, location);
            folders.Add(new(location, entry.Count));
        }
        return new(path, hash, ansiCodePage, folders.ToArray());
    }

    public async Task<PstResult> Import(PstPreview preview, Guid target, IProgress<PstProgress>? progress, CancellationToken token)
    {
        using var source = File.OpenRead(preview.File);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
        if (hash != preview.Fingerprint) throw new InvalidDataException("源文件自预览后已变化，请重新预览并核对目录。");
        var account = (await Targets(token)).SingleOrDefault(m => m.Id == target) ?? throw new InvalidDataException("请选择已打开的本地邮箱。");
        var reportDir = Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "pst-reports");
        Directory.CreateDirectory(reportDir);
        var report = Path.Combine(reportDir, Guid.NewGuid().ToString("N") + ".jsonl");
        using var writer = new StreamWriter(new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var folders = new Dictionary<uint, MailFolder>();
        var counts = new PstProgress(0, 0, 0, 0, "");
        OrganizationStore.Initialize(store);
        using var db = store.Open();
        await foreach (var entry in Read("import", preview.File, preview.AnsiCodePage, token))
        {
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{target:N}/{hash}/{entry.Node}")).AsSpan(0, 16));
            if (entry.Kind == "folder")
            {
                var parent = entry.Parent is uint key ? folders[key] : null;
                var name = Name(entry, preview.File);
                var folder = new MailFolder(id, target, name, (parent?.Path ?? account.Name) + " / " + name, "archive", parent?.Id);
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO folders(id,account_id,name,path,role,parent_id) SELECT $id,$account,$name,$path,'archive',$parent WHERE EXISTS(SELECT 1 FROM accounts WHERE id=$account AND enabled=1 AND removed=0 AND provider='local') ON CONFLICT(id) DO NOTHING";
                cmd.Parameters.AddWithValue("$id", id.ToString());
                cmd.Parameters.AddWithValue("$account", target.ToString());
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$path", folder.Path);
                cmd.Parameters.AddWithValue("$parent", (object?)parent?.Id.ToString() ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                folders.Add(entry.Node, folder);
                continue;
            }
            var destination = folders[entry.Parent!.Value];
            var status = entry.Kind;
            var detail = entry.Detail;
            if (status == "message")
            {
                var metadata = await parser.Parse(entry.Raw, token);
                using var tx = db.BeginTransaction();
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM accounts WHERE id=$account AND enabled=1 AND removed=0 AND provider='local')";
                cmd.Parameters.AddWithValue("$account", target.ToString());
                if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) throw new InvalidDataException("目标邮箱已关闭。");
                cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM messages WHERE id=$id)";
                cmd.Parameters.AddWithValue("$id", id.ToString());
                if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                {
                    cmd.Parameters.Clear();
                    SqliteMailStore.InsertMessage(cmd, destination.Id, entry.Raw, metadata with { Date = entry.Received ?? entry.Submitted ?? metadata.Date }, id, entry.IsRead);
                    if (entry.Categories is { Length: > 0 })
                    {
                        cmd.CommandText = "INSERT INTO mail_marks(folder,item,labels) VALUES($folder,$id,$labels)";
                        cmd.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(entry.Categories));
                        cmd.ExecuteNonQuery();
                    }
                    token.ThrowIfCancellationRequested();
                    tx.Commit();
                    counts = counts with { Imported = counts.Imported + 1 };
                }
                else { status = "duplicate"; counts = counts with { Duplicates = counts.Duplicates + 1 }; }
            }
            else if (status == "skip") counts = counts with { Skipped = counts.Skipped + 1 };
            else counts = counts with { Failed = counts.Failed + 1 };
            counts = counts with { Current = destination.Path + " / " + entry.Name };
            writer.WriteLine(JsonSerializer.Serialize(new { entry.Node, folder = destination.Path, entry.Name, status, detail, id, entry.Received, entry.Submitted, entry.IsRead, entry.Categories }));
            progress?.Report(counts);
        }
        return new(counts, report);
    }

    private static string Name(PstEntry entry, string file) =>
        string.IsNullOrWhiteSpace(entry.Name) ? entry.Parent == null ? Path.GetFileNameWithoutExtension(file) : "未命名目录" : entry.Name;

    private static async IAsyncEnumerable<PstEntry> Read(string mode, string file, int? page, [EnumeratorCancellation] CancellationToken token)
    {
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet.exe"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { typeof(PstEntry).Assembly.Location, mode, file, page?.ToString() ?? "" }) start.ArgumentList.Add(arg);
        using var worker = Process.Start(start) ?? throw new IOException("无法启动 PST 读取器。");
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            while (await worker.StandardOutput.ReadLineAsync(token) is { } line)
                yield return JsonSerializer.Deserialize<PstEntry>(line) ?? throw new InvalidDataException("PST 读取器返回无效记录。");
            await worker.WaitForExitAsync(token);
            if (worker.ExitCode != 0) throw new InvalidDataException("无法读取 PST：" + (await errors).Trim());
        }
        finally
        {
            if (!worker.HasExited) worker.Kill();
            await worker.WaitForExitAsync();
        }
    }
}
