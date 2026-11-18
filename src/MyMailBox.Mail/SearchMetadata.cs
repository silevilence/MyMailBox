using Microsoft.Data.Sqlite;
using MimeKit;
using System.Globalization;

namespace MyMailBox.Mail;

internal sealed class SearchMetadata(SqliteMailStore store)
{
    private const int Version = 1;

    internal static void Initialize(SqliteMailStore store)
    {
        using var db = store.Open(); using var c = db.CreateCommand();
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS search_meta(id TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
                hash TEXT NOT NULL,version INTEGER NOT NULL,complete INTEGER NOT NULL,sent INTEGER,received INTEGER,
                actual_sender TEXT,reply_to TEXT,cc TEXT,bcc TEXT,files TEXT,headers TEXT,priority TEXT,thread TEXT);
            CREATE INDEX IF NOT EXISTS imap_summaries_message ON imap_summaries(message) WHERE message IS NOT NULL;
            """;
        c.ExecuteNonQuery();
    }

    internal void Prepare(CancellationToken token)
    {
        using var db = store.Open();
        using var find = db.CreateCommand(); find.Parameters.AddWithValue("$v", Version);
        find.CommandText = """
            SELECT m.id,m.sha256 FROM messages m LEFT JOIN search_meta x ON x.id=m.id
            WHERE x.id IS NULL OR x.hash<>m.sha256 OR x.version<>$v LIMIT 64
            """;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var ids = new List<(string Id, string Hash)>();
            using (var r = find.ExecuteReader()) while (r.Read()) ids.Add((r.GetString(0), r.GetString(1)));
            if (ids.Count == 0) return;
            foreach (var item in ids)
            {
                token.ThrowIfCancellationRequested();
                using var source = db.CreateCommand();
                source.CommandText = "SELECT raw FROM messages WHERE id=$id AND sha256=$hash";
                source.Parameters.AddWithValue("$id", item.Id); source.Parameters.AddWithValue("$hash", item.Hash);
                if (source.ExecuteScalar() is not byte[] raw) continue;
                using var c = db.CreateCommand();
                c.CommandText = """
                    INSERT OR REPLACE INTO search_meta
                        SELECT id,sha256,$version,$complete,$sent,$received,$actual,$reply,$cc,$bcc,$files,$headers,$priority,$thread
                        FROM messages WHERE id=$id AND sha256=$hash
                    """;
                void Bind(string key, object? value) => c.Parameters.AddWithValue("$" + key, value ?? DBNull.Value);
                Bind("id", item.Id); Bind("hash", item.Hash); Bind("version", Version);
                MimeMessage? mime = null;
                try { using var stream = new MemoryStream(raw, false); mime = MimeMessage.Load(stream, token); }
                catch (FormatException) { }
                using (mime)
                {
                    Bind("complete", mime is null ? 0 : 1);
                    Bind("sent", Stamp(mime?.Headers[HeaderId.Date]));
                    var hops = mime?.Headers.Where(h => h.Id == HeaderId.Received).Select(h => h.Value).FirstOrDefault();
                    var separator = hops?.LastIndexOf(';') ?? -1;
                    Bind("received", separator < 0 ? null : Stamp(hops![(separator + 1)..]));
                    Bind("actual", mime?.Sender?.ToString()); Bind("reply", NonEmpty(mime?.ReplyTo.ToString()));
                    Bind("cc", NonEmpty(mime?.Cc.ToString())); Bind("bcc", NonEmpty(mime?.Bcc.ToString()));
                    Bind("files", mime is null ? null : NonEmpty(string.Join("\n", mime.BodyParts.Select(p => p.ContentDisposition?.FileName ?? p.ContentType.Name).Where(n => !string.IsNullOrWhiteSpace(n)))));
                    Bind("headers", mime is null ? null : NonEmpty(string.Join("\n", mime.Headers.Select(h => h.Field + ": " + h.Value))));
                    var priority = mime is null ? null : mime.Importance == MessageImportance.High || mime.Priority == MessagePriority.Urgent || (int)mime.XPriority < 3 ? "high" :
                        mime.Importance == MessageImportance.Low || mime.Priority == MessagePriority.NonUrgent || (int)mime.XPriority > 3 ? "low" : "normal";
                    Bind("priority", priority);
                    Bind("thread", NonEmpty(mime?.References.FirstOrDefault() ?? mime?.InReplyTo ?? mime?.MessageId));
                    token.ThrowIfCancellationRequested();
                    c.ExecuteNonQuery();
                }
            }
        }
    }
    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static long? Stamp(string? value) => !string.IsNullOrWhiteSpace(value) && MimeKit.Utils.DateUtils.TryParse(value, out var date) ? date.ToUnixTimeMilliseconds() : null;
}
