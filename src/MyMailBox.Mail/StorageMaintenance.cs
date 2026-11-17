using System.Security.Cryptography;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class StorageMaintenance(SqliteMailStore store, IMimeReader parser) : IStorageMaintenance
{
    public Task<StorageReport> Inspect(CancellationToken cancellation = default) => Task.Run(() =>
    {
        using var db = store.Open(); using var transaction = db.BeginTransaction(deferred: true); using var command = db.CreateCommand(); command.Transaction = transaction;
        var problems = new List<string>();
        command.CommandText = "PRAGMA integrity_check";
        using (var reader = command.ExecuteReader()) while (reader.Read()) if (reader.GetString(0) != "ok") problems.Add("数据库：" + reader.GetString(0));
        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = command.ExecuteReader()) while (reader.Read()) problems.Add($"孤立引用：表 {reader.GetString(0)}，行 {reader.GetInt64(1)}；保留待修复。");
        command.CommandText = "SELECT id,raw,size,sha256 FROM messages"; long count = 0, bytes = 0;
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            cancellation.ThrowIfCancellationRequested(); count++; var raw = (byte[])reader[1]; bytes += raw.Length;
            if (raw.Length != reader.GetInt64(2) || reader.IsDBNull(3) || Convert.ToHexString(SHA256.HashData(raw)) != reader.GetString(3)) problems.Add("原文校验不一致：" + reader.GetString(0) + "；保留原文，请从备份恢复。");
        }
        command.CommandText = "SELECT coalesce(sum(length(content)),0) FROM disposable_cache"; var cache = Convert.ToInt64(command.ExecuteScalar());
        transaction.Commit(); return new StorageReport(count, bytes, cache, problems.ToArray());
    }, cancellation);

    public Task<int> Rebuild(CancellationToken cancellation = default) => Task.Run(() =>
    {
        using var db = store.Open(); using var transaction = db.BeginTransaction(); using var query = db.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT id FROM messages ORDER BY id"; var ids = new List<string>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        foreach (var id in ids)
        {
            cancellation.ThrowIfCancellationRequested(); query.Parameters.Clear(); query.CommandText = "SELECT raw,sha256 FROM messages WHERE id=$id"; query.Parameters.AddWithValue("$id", id);
            byte[] raw; string? expected;
            using (var reader = query.ExecuteReader()) { reader.Read(); raw = (byte[])reader[0]; expected = reader.IsDBNull(1) ? null : reader.GetString(1); }
            if (expected != Convert.ToHexString(SHA256.HashData(raw))) throw new InvalidDataException("原文校验失败，重建已回滚：" + id);
            var metadata = parser.Parse(raw, cancellation).GetAwaiter().GetResult();
            query.CommandText = "UPDATE messages SET subject=$subject,sender=$from,recipient=$to,date=$date,size=$size WHERE id=$id";
            query.Parameters.AddWithValue("$subject", metadata.Subject); query.Parameters.AddWithValue("$from", metadata.From); query.Parameters.AddWithValue("$to", metadata.To);
            query.Parameters.AddWithValue("$date", metadata.Date.ToUniversalTime().ToString("O")); query.Parameters.AddWithValue("$size", raw.Length); query.ExecuteNonQuery();
        }
        query.Parameters.Clear(); query.CommandText = "REINDEX messages_folder"; query.ExecuteNonQuery(); cancellation.ThrowIfCancellationRequested(); transaction.Commit(); return ids.Count;
    }, cancellation);

    public Task<long> ClearCache(CancellationToken cancellation = default) => Task.Run(() =>
    {
        using var db = store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT coalesce(sum(length(content)),0) FROM disposable_cache"; var bytes = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "DELETE FROM disposable_cache"; cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); transaction.Commit(); return bytes;
    }, cancellation);
}
