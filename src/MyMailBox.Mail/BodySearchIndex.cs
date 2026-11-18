using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Text;

namespace MyMailBox.Mail;

internal sealed class BodySearchIndex(SqliteMailStore store)
{
    private const int Version = 1;
    internal static void Initialize(SqliteMailStore store)
    {
        using var db = store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS body_index_state(message TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,hash TEXT NOT NULL,version INTEGER NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS body_fts USING fts5(grams, tokenize='ascii', detail=none);
            CREATE TRIGGER IF NOT EXISTS body_index_deleted AFTER DELETE ON body_index_state BEGIN
                DELETE FROM body_fts WHERE rowid=old.rowid;
            END;
            """;
        cmd.ExecuteNonQuery();
    }

    internal void Prepare(bool rebuild, IProgress<SearchIndexProgress>? progress, CancellationToken token)
    {
        using var db = store.Open(); using var cmd = db.CreateCommand();
        if (rebuild) { cmd.CommandText = "DELETE FROM body_index_state"; cmd.ExecuteNonQuery(); }
        cmd.Parameters.AddWithValue("$version", Version);
        const string pending = """
             FROM search_meta x JOIN messages m ON x.id=m.id AND x.hash=m.sha256 LEFT JOIN body_index_state s ON s.message=x.id
             WHERE (s.message IS NULL OR s.hash<>x.hash OR s.version<>$version)
            """;
        cmd.CommandText = "SELECT count(*) " + pending;
        int total = Convert.ToInt32(cmd.ExecuteScalar()), done = 0;
        progress?.Report(new(done, total));
        cmd.Parameters.AddWithValue("$last", "");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            cmd.CommandText = "SELECT x.id,x.hash,x.body " + pending + " AND x.id>$last ORDER BY x.id LIMIT 1";
            string id, hash; string? body;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return;
                id = r.GetString(0); hash = r.GetString(1); body = r.IsDBNull(2) ? null : r.GetString(2);
            }
            cmd.Parameters["$last"].Value = id;
            var grams = body is null ? "" : string.Join(' ', Tokens(body, token));
            using var tx = db.BeginTransaction(); using var write = db.CreateCommand(); write.Transaction = tx;
            write.Parameters.AddWithValue("$id", id); write.Parameters.AddWithValue("$hash", hash);
            write.Parameters.AddWithValue("$version", Version); write.Parameters.AddWithValue("$grams", grams);
            write.CommandText = "DELETE FROM body_index_state WHERE message=$id AND EXISTS(SELECT 1 FROM messages WHERE id=$id AND sha256=$hash)";
            write.ExecuteNonQuery();
            write.CommandText = "INSERT INTO body_index_state SELECT id,sha256,$version FROM messages WHERE id=$id AND sha256=$hash RETURNING rowid";
            if (write.ExecuteScalar() is long row)
            {
                write.CommandText = "INSERT INTO body_fts(rowid,grams) VALUES($row,$grams)"; write.Parameters.AddWithValue("$row", row); write.ExecuteNonQuery();
            }
            tx.Commit(); progress?.Report(new(++done, total));
        }
    }

    // Exact substring verification follows FTS candidate selection. Singles support one-scalar searches,
    // adjacent pairs support Chinese words without dictionaries and phrases crossing word boundaries.
    private static HashSet<string> Tokens(string text, CancellationToken token, int limit = int.MaxValue)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        int previous = -1, offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if ((offset++ & 1023) == 0) token.ThrowIfCancellationRequested();
            int current = Rune.ToUpperInvariant(rune).Value;
            terms.Add($"u{current:x}");
            if (previous >= 0) terms.Add($"p{previous:x}x{current:x}");
            previous = current;
            if (terms.Count >= limit) break;
        }
        return terms;
    }

    internal static string Contains(SqliteCommand cmd, string fieldTest, string value, string parameter)
    {
        if (value.Length == 0) return fieldTest;
        var terms = Tokens(value, default, 32);
        cmd.Parameters.AddWithValue(parameter, string.Join(" AND ", terms));
        return $"(CASE WHEN body IS NULL THEN NULL ELSE localid IN (SELECT message FROM body_index_state WHERE rowid IN (SELECT rowid FROM body_fts WHERE body_fts MATCH {parameter})) AND {fieldTest} END)";
    }
}
