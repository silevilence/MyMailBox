using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class PstImportService(SqliteMailStore store, ILocalMailboxService mailboxes, IMimeReader parser, Func<IReadOnlyList<MailImportReader>>? readers = null) : IPstImport
{
    private readonly PstImportJournal _journal = new(store);
    public Task<IReadOnlyList<PstSession>> Pending(CancellationToken token) => _journal.Pending(token);
    public async Task<IReadOnlyList<LocalMailboxInfo>> Targets(CancellationToken token) =>
        (await mailboxes.List(token)).Where(mailbox => mailbox.Open).ToArray();

    private IEnumerable<MailImportReader> Readers => new[] { PstFileReader.Reader }.Concat(readers?.Invoke() ?? []);
    public IReadOnlyList<ImportFormat> Formats => Readers.Select(r => r.Format).ToArray();
    private MailImportReader Resolve(string id) => Readers.SingleOrDefault(r => r.Format.Id == id) ?? throw new InvalidDataException("导入器已停用，请重新启用后继续。");

    public Task<PstPreview> Preview(string file, int? ansiCodePage, CancellationToken token) =>
        PreviewFile("pst", file, new() { ["codepage"] = ansiCodePage?.ToString() ?? "" }, token);

    public async Task<PstPreview> PreviewFile(string format, string file, Dictionary<string,string> parameters, CancellationToken token)
    {
        var path = Path.GetFullPath(file);
        using var source = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
        var paths = new Dictionary<uint, string>();
        var folders = new List<PstFolderPreview>();
        var reader = Resolve(format);
        await reader.Read(new(path, parameters, true, null, []), (entry, readToken) =>
        {
            readToken.ThrowIfCancellationRequested();
            if (entry.Kind != "folder") throw new InvalidDataException("预览只能返回目录信息。");
            var name = Name(entry, path);
            var location = entry.Parent is uint parent ? paths[parent] + " / " + name : name;
            paths.Add(entry.Node, location);
            folders.Add(new(location, entry.Count));
            return Task.CompletedTask;
        }, token);
        int? codePage = format == "pst" && int.TryParse(parameters.GetValueOrDefault("codepage"), out var page) ? page : null;
        return new(path, hash, codePage, folders.ToArray()) { Format = format, Version = reader.Format.Version, Parameters = new(parameters) };
    }

    public async Task<PstResult> Import(PstPreview preview, Guid target, IProgress<PstProgress>? progress, CancellationToken token)
    {
        using var source = File.OpenRead(preview.File);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
        if (hash != preview.Fingerprint) throw new InvalidDataException("源文件自预览后已变化，请重新预览并核对目录。");
        var reader = Resolve(preview.Format);
        if (reader.Format.Version != preview.Version) throw new InvalidDataException("导入格式版本已变化，请重新预览。");
        // Preserve IDs assigned by the original PST importer. Other formats own independent node namespaces.
        var identity = preview.Format == "pst" ? hash : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { hash, preview.Format, preview.Version, parameters = preview.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal) }))));
        var account = (await Targets(token)).SingleOrDefault(m => m.Id == target) ?? throw new InvalidDataException("请选择已打开的本地邮箱。");
        var reportDir = Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "pst-reports");
        Directory.CreateDirectory(reportDir);
        var report = Path.Combine(reportDir, Guid.NewGuid().ToString("N") + ".jsonl");
        using var writer = new StreamWriter(new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var folders = new Dictionary<uint, MailFolder>();
        var counts = new PstProgress(0, 0, 0, 0, "");
        OrganizationStore.Initialize(store);
        using var db = store.Open();
        var committed = PstImportJournal.Start(db, target, preview, identity, report);
        await reader.Read(new(preview.File, preview.Parameters, false, target, committed), Consume, token);
        if (counts.Failed == 0) PstImportJournal.Finish(db, report);
        return new(counts, report);

        async Task Consume(MailImportItem entry, CancellationToken readToken)
        {
            readToken.ThrowIfCancellationRequested();
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{target:N}/{identity}/{entry.Node}")).AsSpan(0, 16));
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
                return;
            }
            var destination = folders[entry.Parent!.Value];
            var status = entry.Kind;
            var detail = entry.Detail;
            ParsedMail? metadata = null;
            if (status == "message")
            {
                try { metadata = await parser.Parse(entry.Raw ?? throw new InvalidDataException("导入器未提供邮件原文。"), readToken); }
                catch (Exception e) when (e is FormatException or InvalidDataException) { status = "error"; detail = e.Message; }
            }
            if (metadata != null)
            {
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
                    SqliteMailStore.InsertMessage(cmd, destination.Id, entry.Raw!, metadata with { Date = entry.Received ?? entry.Submitted ?? metadata.Date }, id, entry.IsRead);
                    if (entry.Categories is { Length: > 0 })
                    {
                        cmd.CommandText = "INSERT INTO mail_marks(folder,item,labels) VALUES($folder,$id,$labels)";
                        cmd.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(entry.Categories));
                        cmd.ExecuteNonQuery();
                    }
                    counts = counts with { Imported = counts.Imported + 1 };
                }
                else { status = "duplicate"; counts = counts with { Duplicates = counts.Duplicates + 1 }; }
                PstImportJournal.CommitItem(cmd, target, identity, entry.Node);
                readToken.ThrowIfCancellationRequested();
                tx.Commit();
            }
            else if (status == "duplicate") counts = counts with { Duplicates = counts.Duplicates + 1 };
            else if (status == "skip") counts = counts with { Skipped = counts.Skipped + 1 };
            else counts = counts with { Failed = counts.Failed + 1 };
            counts = counts with { Current = destination.Path + " / " + entry.Name };
            writer.WriteLine(JsonSerializer.Serialize(new { entry.Node, folder = destination.Path, entry.Name, status, detail, id, entry.Received, entry.Submitted, entry.IsRead, entry.Categories }));
            progress?.Report(counts);
        }
    }

    private static string Name(MailImportItem entry, string file) =>
        string.IsNullOrWhiteSpace(entry.Name) ? entry.Parent == null ? Path.GetFileNameWithoutExtension(file) : "未命名目录" : entry.Name;

}
