using Microsoft.Data.Sqlite;
using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

internal sealed class PstImportJournal
{
    private readonly SqliteMailStore _store;
    internal PstImportJournal(SqliteMailStore store)
    {
        _store = store;
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS pst_sessions(
                target TEXT NOT NULL, identity TEXT NOT NULL, fingerprint TEXT NOT NULL,
                format TEXT NOT NULL, version INTEGER NOT NULL, parameters TEXT NOT NULL,
                path TEXT NOT NULL, report TEXT NOT NULL, pending INTEGER NOT NULL,
                PRIMARY KEY(target,identity));
            CREATE TABLE IF NOT EXISTS pst_committed(
                target TEXT NOT NULL, fingerprint TEXT NOT NULL, node INTEGER NOT NULL,
                PRIMARY KEY(target,fingerprint,node));
            """;
        command.ExecuteNonQuery();
    }

    internal Task<IReadOnlyList<PstSession>> Pending(CancellationToken token) => Task.Run<IReadOnlyList<PstSession>>(() =>
    {
        using var db = _store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT s.path,s.fingerprint,a.id,a.name,
                   (SELECT COUNT(*) FROM pst_committed c WHERE c.target=s.target AND c.fingerprint=s.identity),s.report,s.format,s.version,s.parameters
            FROM pst_sessions s JOIN accounts a ON a.id=s.target
            WHERE s.pending=1 AND a.enabled=1 AND a.removed=0 ORDER BY a.name,s.path
            """;
        using var rows = cmd.ExecuteReader();
        var result = new List<PstSession>();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            var parameters = JsonSerializer.Deserialize<Dictionary<string,string>>(rows.GetString(8))!;
            result.Add(new(rows.GetString(0), rows.GetString(1), int.TryParse(parameters.GetValueOrDefault("codepage"), out var cp) ? cp : null,
                Guid.Parse(rows.GetString(2)), rows.GetString(3), rows.GetInt32(4), rows.GetString(5), rows.GetString(6), rows.GetInt32(7), parameters));
        }
        return result;
    }, token);

    internal static List<uint> Start(SqliteConnection db, Guid target, PstPreview file, string identity, string report)
    {
        using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$target", target.ToString());
        cmd.Parameters.AddWithValue("$hash", file.Fingerprint);
        cmd.Parameters.AddWithValue("$identity", identity);
        cmd.Parameters.AddWithValue("$format", file.Format);
        cmd.Parameters.AddWithValue("$version", file.Version);
        cmd.Parameters.AddWithValue("$parameters", JsonSerializer.Serialize(file.Parameters));
        cmd.Parameters.AddWithValue("$path", file.File);
        cmd.Parameters.AddWithValue("$report", report);
        cmd.CommandText = """
            INSERT INTO pst_sessions VALUES($target,$identity,$hash,$format,$version,$parameters,$path,$report,1)
            ON CONFLICT(target,identity) DO UPDATE SET path=$path,report=$report,parameters=$parameters,pending=1
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT node FROM pst_committed WHERE target=$target AND fingerprint=$identity";
        using var rows = cmd.ExecuteReader();
        var nodes = new List<uint>();
        while (rows.Read()) nodes.Add(checked((uint)rows.GetInt64(0)));
        return nodes;
    }

    internal static void CommitItem(SqliteCommand cmd, Guid target, string hash, uint node)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = "INSERT OR IGNORE INTO pst_committed VALUES($target,$hash,$node)";
        cmd.Parameters.AddWithValue("$target", target.ToString());
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$node", (long)node);
        cmd.ExecuteNonQuery();
    }
    internal static void Finish(SqliteConnection db, string report)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE pst_sessions SET pending=0 WHERE report=$report";
        cmd.Parameters.AddWithValue("$report", report);
        cmd.ExecuteNonQuery();
    }
}
