using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

internal sealed class GraphDeltaStore(SqliteMailStore store, Guid account, string home)
{
    internal sealed record Cursor(string? Link, long Generation, bool Rebuilding);
    internal static void Initialize(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS graph_scopes(account TEXT NOT NULL,home TEXT NOT NULL,folder TEXT NOT NULL,name TEXT NOT NULL,
                cursor TEXT,generation INTEGER NOT NULL,rebuilding INTEGER NOT NULL,last_success TEXT,PRIMARY KEY(account,home,folder));
            CREATE TABLE IF NOT EXISTS graph_items(account TEXT NOT NULL,home TEXT NOT NULL,folder TEXT NOT NULL,id TEXT NOT NULL,
                data TEXT NOT NULL,date TEXT NOT NULL,generation INTEGER NOT NULL,PRIMARY KEY(account,home,folder,id));
            CREATE INDEX IF NOT EXISTS graph_items_date ON graph_items(account,home,folder,date DESC,id);
            CREATE TABLE IF NOT EXISTS graph_cooldown(account TEXT NOT NULL,home TEXT NOT NULL,until_utc INTEGER NOT NULL,PRIMARY KEY(account,home));
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(SqliteConnection db, string? folder = null)
    {
        var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$a", account.ToString());
        cmd.Parameters.AddWithValue("$h", home);
        if (folder != null) cmd.Parameters.AddWithValue("$f", folder);
        return cmd;
    }
    internal Cursor Begin(GraphFolder folder)
    {
        using var db = store.Open();
        using var cmd = Command(db, folder.Id);
        cmd.CommandText = """
            INSERT INTO graph_scopes VALUES($a,$h,$f,$name,NULL,1,1,NULL)
            ON CONFLICT(account,home,folder) DO UPDATE SET name=$name;
            """;
        cmd.Parameters.AddWithValue("$name", folder.Name);
        cmd.ExecuteNonQuery();
        return Current(cmd);
    }
    private static Cursor Current(SqliteCommand cmd)
    {
        cmd.CommandText = "SELECT cursor,generation,rebuilding FROM graph_scopes WHERE account=$a AND home=$h AND folder=$f";
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("同步目录已移除。");
        return new(row.IsDBNull(0) ? null : row.GetString(0), row.GetInt64(1), row.GetBoolean(2));
    }
    internal void Remove(string folder, Cursor prior)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var cmd = Command(db, folder); cmd.Transaction = tx;
        if (Current(cmd) != prior) throw new InvalidDataException("同步进度已变化，请重新确认目录。");
        cmd.CommandText = "DELETE FROM graph_items WHERE account=$a AND home=$h AND folder=$f; DELETE FROM graph_scopes WHERE account=$a AND home=$h AND folder=$f";
        cmd.ExecuteNonQuery(); tx.Commit();
    }
    internal Cursor Reset(string folder, Cursor prior)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var cmd = Command(db, folder); cmd.Transaction = tx;
        if (Current(cmd) != prior) throw new InvalidDataException("此目录的同步进度已更新，请重新同步。");
        cmd.CommandText = "UPDATE graph_scopes SET cursor=NULL,generation=generation+1,rebuilding=1 WHERE account=$a AND home=$h AND folder=$f";
        cmd.ExecuteNonQuery(); tx.Commit();
        return new(null, prior.Generation + 1, true);
    }

    internal Cursor Apply(string folder, Cursor prior, JsonElement items, string link, bool final, CancellationToken token)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var cmd = Command(db, folder); cmd.Transaction = tx;
        if (Current(cmd) != prior) throw new InvalidDataException("同步页已由另一操作提交，请重新同步。");
        cmd.Parameters.AddWithValue("$gen", prior.Generation);
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var data = cmd.Parameters.Add("$data", SqliteType.Text);
        var date = cmd.Parameters.Add("$date", SqliteType.Text);
        foreach (var item in items.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            id.Value = item.GetProperty("id").GetString() ?? throw new InvalidDataException("增量记录缺少邮件 ID。");
            if (item.TryGetProperty("@removed", out _))
            {
                cmd.CommandText = "DELETE FROM graph_items WHERE account=$a AND home=$h AND folder=$f AND id=$id";
                cmd.ExecuteNonQuery();
                continue;
            }
            cmd.CommandText = "SELECT data FROM graph_items WHERE account=$a AND home=$h AND folder=$f AND id=$id";
            var old = cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<GraphSummary>(json) : null;
            var summary = new GraphSummary((string)id.Value,
                item.TryGetProperty("subject", out var subject) ? subject.GetString() ?? "" : old?.Subject ?? "",
                item.TryGetProperty("from", out _) ? GraphMailService.Sender(item) : old?.From ?? "",
                item.TryGetProperty("receivedDateTime", out var received) ? (received.ValueKind == JsonValueKind.String ? received.GetDateTimeOffset() : null) : old?.Date,
                item.TryGetProperty("isRead", out var read) ? read.GetBoolean() : old?.IsRead ?? false,
                item.TryGetProperty("hasAttachments", out var atts) ? atts.GetBoolean() : old?.HasAttachments ?? false);
            data.Value = JsonSerializer.Serialize(summary);
            date.Value = summary.Date?.ToUniversalTime().ToString("O") ?? "";
            cmd.CommandText = """
                INSERT INTO graph_items VALUES($a,$h,$f,$id,$data,$date,$gen)
                ON CONFLICT(account,home,folder,id) DO UPDATE SET data=$data,date=$date,generation=$gen
                """;
            cmd.ExecuteNonQuery();
        }
        if (final && prior.Rebuilding)
        {
            cmd.CommandText = "DELETE FROM graph_items WHERE account=$a AND home=$h AND folder=$f AND generation<>$gen";
            cmd.ExecuteNonQuery();
        }
        cmd.Parameters.AddWithValue("$cursor", link);
        cmd.Parameters.AddWithValue("$done", final);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.CommandText = """
            UPDATE graph_scopes SET cursor=$cursor,rebuilding=CASE WHEN $done THEN 0 ELSE rebuilding END,
                last_success=CASE WHEN $done THEN $now ELSE last_success END WHERE account=$a AND home=$h AND folder=$f
            """;
        cmd.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit();
        return new(link, prior.Generation, prior.Rebuilding && !final);
    }

    internal IReadOnlyList<GraphSyncScope> Scopes()
    {
        using var db = store.Open(); using var cmd = Command(db);
        cmd.CommandText = """
            SELECT folder,name,last_success,(SELECT COUNT(*) FROM graph_items i WHERE i.account=s.account AND i.home=s.home AND i.folder=s.folder)
            FROM graph_scopes s WHERE account=$a AND home=$h ORDER BY name,folder
            """;
        using var row = cmd.ExecuteReader(); var scopes = new List<GraphSyncScope>();
        while (row.Read()) scopes.Add(new(row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : DateTimeOffset.Parse(row.GetString(2)), row.GetInt32(3)));
        return scopes;
    }
    internal GraphOfflinePage Cached(string folder, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var db = store.Open(); using var cmd = Command(db, folder);
        cmd.CommandText = "SELECT data FROM graph_items WHERE account=$a AND home=$h AND folder=$f ORDER BY date DESC,id LIMIT 26 OFFSET $offset";
        cmd.Parameters.AddWithValue("$offset", offset);
        using var rows = cmd.ExecuteReader(); var items = new List<GraphSummary>();
        while (rows.Read()) items.Add(JsonSerializer.Deserialize<GraphSummary>(rows.GetString(0))!);
        return new(items.Take(25).ToArray(), items.Count > 25);
    }
    internal void CheckCooldown()
    {
        using var db = store.Open(); using var cmd = Command(db);
        cmd.CommandText = "SELECT until_utc FROM graph_cooldown WHERE account=$a AND home=$h";
        if (cmd.ExecuteScalar() is long seconds && DateTimeOffset.FromUnixTimeSeconds(seconds) is var until && until > DateTimeOffset.UtcNow)
            throw new InvalidDataException($"微软服务正在限流，请在 {until.ToLocalTime():HH:mm:ss} 后重试。");
    }
    internal void Cooldown(DateTimeOffset until)
    {
        using var db = store.Open(); using var cmd = Command(db);
        cmd.Parameters.AddWithValue("$until", until.ToUnixTimeSeconds() + 1);
        cmd.CommandText = "INSERT INTO graph_cooldown VALUES($a,$h,$until) ON CONFLICT(account,home) DO UPDATE SET until_utc=MAX(until_utc,$until)";
        cmd.ExecuteNonQuery();
    }
}
