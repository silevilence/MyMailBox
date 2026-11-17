using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class MailArchive : IMailArchive
{
    private readonly SqliteMailStore store;
    public MailArchive(SqliteMailStore store) { this.store = store; _ = new AccountService(store); }
    public Task<string> Export(Guid[] folders, string destination, CancellationToken cancellation = default) => Task.Run(async () =>
    {
        if (folders.Length == 0) throw new InvalidDataException("归档范围为空，请先选择目录。");
        destination = Path.GetFullPath(destination); if (File.Exists(destination)) throw new IOException("目标文件已存在，请使用新名称。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using var db = store.Open(); using var transaction = db.BeginTransaction(deferred: true);
            using var query = db.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT id,account_id,name,path,role,parent_id FROM folders"; var allFolders = new List<MailFolder>();
            using (var reader = query.ExecuteReader()) while (reader.Read()) allFolders.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5))));
            if (folders.Any(id => !allFolders.Any(f => f.Id == id))) throw new InvalidDataException("所选目录已不存在，请重新选择。");
            var selected = allFolders.Where(f => folders.Contains(f.Id)).ToList();
            for (var i = 0; i < selected.Count; i++)
                if (selected[i].ParentId is { } parent && selected.All(f => f.Id != parent)) selected.Add(allFolders.Single(f => f.Id == parent));
            var accounts = new List<MailAccount>();
            foreach (var id in selected.Select(f => f.AccountId).Distinct())
            {
                query.CommandText = "SELECT * FROM accounts WHERE id=$id"; query.Parameters.Clear(); query.Parameters.AddWithValue("$id", id.ToString());
                using var reader = query.ExecuteReader();
                if (!reader.Read()) throw new InvalidDataException("目录缺少所属账户，请先检查工作空间。");
                accounts.Add(new(id, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), JsonSerializer.Deserialize<string[]>(reader.GetString(5))!, reader.GetString(6), reader.GetString(7), reader.GetInt32(8), false, (AccountCapabilities)reader.GetInt32(10), null, "归档副本，无授权"));
            }
            var messages = new List<ArchiveMessage>();
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var folder in folders.Distinct())
                {
                    query.CommandText = "SELECT id,subject,sender,recipient,date,size,is_read,raw,sha256 FROM messages WHERE folder_id=$id ORDER BY id"; query.Parameters.Clear(); query.Parameters.AddWithValue("$id", folder.ToString());
                    using var reader = query.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellation.ThrowIfCancellationRequested(); var raw = (byte[])reader[7]; var hash = Convert.ToHexString(SHA256.HashData(raw));
                        if (reader.IsDBNull(8) || hash != reader.GetString(8)) throw new InvalidDataException("原文校验不一致，归档未发布。");
                        var summary = new MailSummary(Guid.Parse(reader.GetString(0)), folder, reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt64(5), reader.GetBoolean(6));
                        var name = "messages/" + summary.Id.ToString("N") + ".eml"; var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                        await using (var output = entry.Open()) await output.WriteAsync(raw, cancellation);
                        messages.Add(new(summary, name, hash));
                    }
                }
                var manifest = zip.CreateEntry("manifest.json"); await using var outputManifest = manifest.Open();
                await JsonSerializer.SerializeAsync(outputManifest, new MailArchiveManifest(1, accounts.ToArray(), selected.ToArray(), messages.ToArray()), cancellationToken: cancellation);
            }
            transaction.Commit(); cancellation.ThrowIfCancellationRequested(); File.Move(temporary, destination); return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancellation);

    public Task<ArchiveImportResult> Import(string archive, CancellationToken cancellation = default) => Task.Run(async () =>
    {
        using var zip = ZipFile.OpenRead(archive); var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少邮件归档清单；工作空间备份请使用恢复入口。");
        if (manifestEntry.Length > 32 * 1024 * 1024) throw new InvalidDataException("归档清单过大。");
        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<MailArchiveManifest>(manifestStream, cancellationToken: cancellation) ?? throw new InvalidDataException("归档清单为空。");
        if (manifest.Schema != 1 || manifest.Accounts is null || manifest.Folders is null || manifest.Messages is null) throw new InvalidDataException("不支持的邮件归档版本。");
        if (manifest.Accounts.Any(a => string.IsNullOrWhiteSpace(a.Name) || a.Tags is null || !System.Text.RegularExpressions.Regex.IsMatch(a.Color ?? "", "^#[0-9a-fA-F]{6}$"))) throw new InvalidDataException("归档账户元数据无效。");
        if (manifest.Accounts.Select(a => a.Id).Distinct().Count() != manifest.Accounts.Length || manifest.Folders.Select(f => f.Id).Distinct().Count() != manifest.Folders.Length || manifest.Messages.Select(m => m.Summary.Id).Distinct().Count() != manifest.Messages.Length) throw new InvalidDataException("归档包含重复标识。");
        if (zip.Entries.Count != manifest.Messages.Length + 1 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count) throw new InvalidDataException("归档包含未声明或重复内容。");
        using var db = store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        var accountMap = new Dictionary<Guid, Guid>(); var folderMap = new Dictionary<Guid, Guid>(); var messageMap = new Dictionary<Guid, Guid>(); var notes = new List<string>();
        var names = new Dictionary<Guid, string>();
        foreach (var account in manifest.Accounts)
        {
            cancellation.ThrowIfCancellationRequested(); var id = Map("accounts", account.Id); accountMap.Add(account.Id, id);
            var name = account.Name + (id != account.Id ? "（导入）" : ""); names.Add(account.Id, name);
            var alias = account.Alias;
            if (alias is not null)
            {
                command.Parameters.Clear(); command.CommandText = "SELECT count(*) FROM accounts WHERE alias=$alias COLLATE NOCASE AND removed=0"; command.Parameters.AddWithValue("$alias", alias);
                if (Convert.ToInt32(command.ExecuteScalar()) > 0) { alias = "import-" + id.ToString("N"); notes.Add(account.Name + "：alias 冲突，已改为 " + alias); }
            }
            command.Parameters.Clear(); command.CommandText = "INSERT INTO accounts VALUES($id,$name,'local',$address,$alias,$tags,$color,$notes,$order,1,12,NULL,'无需登录',0)";
            Put("$id", id.ToString()); Put("$name", name); Put("$address", account.Address); Put("$alias", alias); Put("$tags", JsonSerializer.Serialize(account.Tags)); Put("$color", account.Color); Put("$notes", account.Notes); Put("$order", account.Order); command.ExecuteNonQuery();
        }
        foreach (var folder in manifest.Folders) folderMap.Add(folder.Id, Map("folders", folder.Id));
        foreach (var folder in manifest.Folders)
        {
            if (!accountMap.TryGetValue(folder.AccountId, out var account) || folder.ParentId is { } parent && !folderMap.ContainsKey(parent)) throw new InvalidDataException("归档目录引用不完整。");
            // Validate hierarchy before exposing it to tree-based UIs.
            var visited = new HashSet<Guid> { folder.Id }; var ancestor = folder.ParentId;
            while (ancestor is { } next) { if (!visited.Add(next)) throw new InvalidDataException("归档目录包含循环。"); var entry = manifest.Folders.Single(f => f.Id == next); if (entry.AccountId != folder.AccountId) throw new InvalidDataException("目录父级属于不同账户。"); ancestor = entry.ParentId; }
            command.Parameters.Clear(); command.CommandText = "INSERT INTO folders VALUES($id,$account,$name,$path,$role,$parent)";
            Put("$id", folderMap[folder.Id].ToString()); Put("$account", account.ToString()); Put("$name", folder.Name); Put("$path", names[folder.AccountId] + " / " + string.Join(" / ", folder.Path.Split(" / ").Skip(1))); Put("$role", folder.Role); Put("$parent", folder.ParentId is { } p ? folderMap[p].ToString() : null); command.ExecuteNonQuery();
        }
        foreach (var item in manifest.Messages)
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Entry != "messages/" + item.Summary.Id.ToString("N") + ".eml" || !folderMap.ContainsKey(item.Summary.FolderId)) throw new InvalidDataException("邮件归档路径或目录引用无效。");
            var entry = zip.GetEntry(item.Entry) ?? throw new InvalidDataException("缺少邮件原文。");
            if (entry.Length != item.Summary.Size || entry.Length is < 1 or > MimeReader.MaxMessageBytes) throw new InvalidDataException("邮件长度无效。");
            var raw = new byte[checked((int)entry.Length)]; await using (var input = entry.Open()) await input.ReadExactlyAsync(raw, cancellation);
            if (Convert.ToHexString(SHA256.HashData(raw)) != item.Sha256) throw new InvalidDataException("邮件原文校验失败，导入已回滚。");
            var id = Map("messages", item.Summary.Id); messageMap.Add(item.Summary.Id, id);
            command.Parameters.Clear(); command.CommandText = "INSERT INTO messages(id,folder_id,subject,sender,recipient,date,size,is_read,raw,sha256) VALUES($id,$folder,$subject,$from,$to,$date,$size,$read,$raw,$hash)";
            Put("$id", id.ToString()); Put("$folder", folderMap[item.Summary.FolderId].ToString()); Put("$subject", item.Summary.Subject); Put("$from", item.Summary.From); Put("$to", item.Summary.To); Put("$date", item.Summary.Date.ToUniversalTime().ToString("O")); Put("$size", raw.Length); Put("$read", item.Summary.IsRead); Put("$raw", raw); Put("$hash", item.Sha256); command.ExecuteNonQuery();
        }
        cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        notes.Add("导入为独立本地归档，不包含授权凭据；已有账户和邮件未覆盖。");
        return new ArchiveImportResult(accountMap.Count, folderMap.Count, messageMap.Count, accountMap, folderMap, messageMap, notes.ToArray());

        void Put(string key, object? value) => command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        Guid Map(string table, Guid original)
        {
            command.Parameters.Clear(); command.CommandText = $"SELECT count(*) FROM {table} WHERE id=$id"; Put("$id", original.ToString());
            return Convert.ToInt32(command.ExecuteScalar()) == 0 ? original : Guid.NewGuid();
        }
    }, cancellation);
}
