using Microsoft.Data.Sqlite;
using MimeKit;
using System.Globalization;

namespace MyMailBox.Mail;

internal sealed class SearchMetadata(SqliteMailStore store)
{
    private const int Version = 2;

    internal static void Initialize(SqliteMailStore store)
    {
        using var db = store.Open();using var tx=db.BeginTransaction();using var c = db.CreateCommand();c.Transaction=tx;
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS search_meta(id TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
                hash TEXT NOT NULL,version INTEGER NOT NULL,complete INTEGER NOT NULL,sent INTEGER,received INTEGER,
                actual_sender TEXT,reply_to TEXT,cc TEXT,bcc TEXT,files TEXT,headers TEXT,priority TEXT,thread TEXT);
            CREATE INDEX IF NOT EXISTS imap_summaries_message ON imap_summaries(message) WHERE message IS NOT NULL;
            """;
        c.ExecuteNonQuery();
        c.CommandText = "SELECT count(*) FROM pragma_table_info('search_meta') WHERE name='body'";
        if ((long)c.ExecuteScalar()! == 0)
        {
            c.CommandText = "ALTER TABLE search_meta ADD COLUMN body TEXT"; c.ExecuteNonQuery();
        }
        c.CommandText="SELECT count(*) FROM sqlite_master WHERE name='search_pending_v2'";
        bool seed=(long)c.ExecuteScalar()! ==0;
        c.CommandText="""
            CREATE TABLE IF NOT EXISTS search_pending_v2(id TEXT PRIMARY KEY);
            CREATE INDEX IF NOT EXISTS search_message_coverage ON messages(folder_id,id,sha256,subject);
            CREATE INDEX IF NOT EXISTS search_meta_coverage ON search_meta(id,hash,complete,(body IS NOT NULL));
            CREATE INDEX IF NOT EXISTS search_meta_dates ON search_meta(sent DESC,id);
            CREATE TRIGGER IF NOT EXISTS search_pending_add AFTER INSERT ON messages BEGIN INSERT OR IGNORE INTO search_pending_v2 VALUES(new.id); END;
            CREATE TRIGGER IF NOT EXISTS search_pending_change AFTER UPDATE OF sha256 ON messages WHEN new.sha256 IS NOT old.sha256 BEGIN INSERT OR IGNORE INTO search_pending_v2 VALUES(new.id); END;
            CREATE TRIGGER IF NOT EXISTS search_pending_remove AFTER DELETE ON messages BEGIN DELETE FROM search_pending_v2 WHERE id=old.id; END;
            CREATE TRIGGER IF NOT EXISTS search_meta_stale AFTER UPDATE OF version,hash ON search_meta BEGIN INSERT OR IGNORE INTO search_pending_v2 VALUES(new.id); END;
            """;
        c.ExecuteNonQuery();
        if(seed){c.CommandText="INSERT OR IGNORE INTO search_pending_v2 SELECT m.id FROM messages m LEFT JOIN search_meta x ON x.id=m.id WHERE x.id IS NULL OR x.hash<>m.sha256 OR x.version<>$v";c.Parameters.AddWithValue("$v",Version);c.ExecuteNonQuery();}
        tx.Commit();
    }

    internal void Prepare(CancellationToken token,SqliteConnection? preview=null)
    {
        using var owned=preview is null ? store.Open() : null;
        var db=preview ?? owned!;
        using var find = db.CreateCommand();
        const string pending = "SELECT m.id,m.sha256 FROM search_pending_v2 p CROSS JOIN messages m ON m.id=p.id";
        find.CommandText = preview is null
            ? pending + " LIMIT 64"
            : pending + " WHERE NOT EXISTS(SELECT 1 FROM temp.search_meta t WHERE t.id=p.id AND t.hash=m.sha256) LIMIT 64";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var ids = new List<(string Id, string Hash)>();
            using (var r = find.ExecuteReader()) while (r.Read()) ids.Add((r.GetString(0), r.GetString(1)));
            if (ids.Count == 0) return;
            using var batch=db.BeginTransaction(deferred:preview is not null);
            foreach (var item in ids)
            {
                token.ThrowIfCancellationRequested();
                using var source = db.CreateCommand();source.Transaction=batch;
                source.CommandText = "SELECT raw FROM messages WHERE id=$id AND sha256=$hash";
                source.Parameters.AddWithValue("$id", item.Id); source.Parameters.AddWithValue("$hash", item.Hash);
                if (source.ExecuteScalar() is not byte[] raw) continue;
                using var c = db.CreateCommand();c.Transaction=batch;
                c.CommandText = """
                    INSERT OR REPLACE INTO search_meta
                        SELECT id,sha256,$version,$complete,$sent,$received,$actual,$reply,$cc,$bcc,$files,$headers,$priority,$thread,$body
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
                    string? body = mime?.TextBody;
                    if (string.IsNullOrWhiteSpace(body) && mime?.HtmlBody is { } html)
                    {
                        using var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
                        foreach (var active in doc.QuerySelectorAll("script,style,head")) active.Remove();
                        body = doc.Body?.TextContent;
                    }
                    Bind("body", NonEmpty(body));
                    Bind("thread", NonEmpty(mime?.References.FirstOrDefault() ?? mime?.InReplyTo ?? mime?.MessageId));
                    token.ThrowIfCancellationRequested();
                    c.ExecuteNonQuery();
                    if(preview is null){c.CommandText="DELETE FROM search_pending_v2 WHERE id=$id AND EXISTS(SELECT 1 FROM messages WHERE id=$id AND sha256=$hash)";c.ExecuteNonQuery();}
                }
            }
            batch.Commit();
        }
    }
    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static long? Stamp(string? value) => !string.IsNullOrWhiteSpace(value) && MimeKit.Utils.DateUtils.TryParse(value, out var date) ? date.ToUnixTimeMilliseconds() : null;
}
