using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class MailboxReceiveProvider : IReceiveProvider
{
    private readonly SqliteMailStore _store;
    private readonly Func<IReadOnlyList<IMailboxProvider>> _providers;
    public MailboxReceiveProvider(SqliteMailStore store, Func<IReadOnlyList<IMailboxProvider>> providers)
    {
        _store = store;
        _providers = providers;
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provider_inbox_sync(
                account TEXT PRIMARY KEY, provider TEXT NOT NULL, folder TEXT NOT NULL, cursor TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS provider_inbox(
                account TEXT NOT NULL, provider TEXT NOT NULL, folder TEXT NOT NULL, item TEXT NOT NULL,
                key TEXT NOT NULL UNIQUE, stamp INTEGER NOT NULL, subject TEXT NOT NULL, sender TEXT NOT NULL, raw BLOB,
                PRIMARY KEY(account,provider,folder,item));
            CREATE INDEX IF NOT EXISTS provider_inbox_order ON provider_inbox(account,provider,stamp DESC,key DESC);
            """;
        command.ExecuteNonQuery();
    }
    private IMailboxProvider? Find(MailAccount account) => (account.Provider.Contains('/') || account.Provider=="exchange-eas") ?
        _providers().SingleOrDefault(p => p.Type.Id == account.Provider && p.Type.Features.HasFlag(MailboxFeatures.Delta)) : null;
    public bool Supports(MailAccount account) => Find(account) != null;
    public bool CanReadCache(MailAccount account) => account.Provider.Contains('/') || account.Provider == "exchange-eas";
    private static MailboxIdentity Identity(MailAccount a) => new(a.Id,a.Address);
    private static SqliteCommand Command(SqliteConnection db,MailAccount a,string sql)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$a",a.Id.ToString()); cmd.Parameters.AddWithValue("$p",a.Provider);
        return cmd;
    }
    public async Task Receive(MailAccount account,int range,CancellationToken token)
    {
        var p = Find(account) ?? throw new InvalidDataException("需要启用接收插件。");
        var identity = Identity(account);
        var folder = (await p.Folders(identity,token)).SingleOrDefault(f => f.Role == "inbox") ?? throw new InvalidDataException("提供者缺少收件箱。");
        using var db = _store.Open();
        using var state = Command(db,account,"SELECT cursor FROM provider_inbox_sync WHERE account=$a AND provider=$p AND folder=$folder");
        state.Parameters.AddWithValue("$folder",folder.Id);
        var cursor = state.ExecuteScalar() as string;
        // Save each page with its continuation. A stopped plugin can resume at the last committed page.
        for (var remaining = range; remaining > 0;)
        {
            var count = Math.Min(remaining,100);
            var page = await p.Delta(identity,folder.Id,cursor,count,token);
            if (page.More && page.Cursor == cursor) throw new InvalidDataException("接收插件返回了重复游标。");
            using var tx = db.BeginTransaction();
            using var cmd = Command(db,account,"");
            cmd.Transaction = tx; cmd.Parameters.AddWithValue("$folder",folder.Id);
            cmd.CommandText = "DELETE FROM provider_inbox WHERE account=$a AND (provider<>$p OR folder<>$folder)";
            cmd.ExecuteNonQuery();
            var item = cmd.Parameters.Add("$item",SqliteType.Text);
            cmd.CommandText = "DELETE FROM provider_inbox WHERE account=$a AND provider=$p AND folder=$folder AND item=$item";
            foreach (var removed in page.Removed) { item.Value = removed; cmd.ExecuteNonQuery(); }
            var stamp = cmd.Parameters.Add("$stamp",SqliteType.Integer);
            var subject = cmd.Parameters.Add("$subject",SqliteType.Text);
            var sender = cmd.Parameters.Add("$sender",SqliteType.Text);
            var key = cmd.Parameters.Add("$key",SqliteType.Text);
            cmd.CommandText = """
                INSERT INTO provider_inbox(account,provider,folder,item,key,stamp,subject,sender) VALUES($a,$p,$folder,$item,$key,$stamp,$subject,$sender)
                ON CONFLICT(account,provider,folder,item) DO UPDATE SET stamp=excluded.stamp,subject=excluded.subject,sender=excluded.sender,raw=NULL
                """;
            foreach (var changed in page.Changed)
            {
                token.ThrowIfCancellationRequested(); item.Value = changed.Id;
                stamp.Value = changed.Date?.ToUnixTimeMilliseconds() ?? 0; subject.Value = changed.Subject; sender.Value = changed.Sender;
                key.Value = Guid.NewGuid().ToString("N"); cmd.ExecuteNonQuery();
            }
            cmd.CommandText = """
                INSERT INTO provider_inbox_sync(account,provider,folder,cursor) VALUES($a,$p,$folder,$cursor)
                ON CONFLICT(account) DO UPDATE SET provider=$p,folder=$folder,cursor=$cursor
                """;
            cmd.Parameters.AddWithValue("$cursor",page.Cursor);
            cmd.ExecuteNonQuery(); tx.Commit();
            if (!page.More) break;
            cursor = page.Cursor; remaining -= count;
        }
    }
    public Task<IReadOnlyList<InboxMessage>> Inbox(MailAccount a,InboxPosition? before,int limit,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var db = _store.Open();
        using var cmd = Command(db,a,"""
            SELECT stamp,key,subject,sender,raw IS NOT NULL FROM provider_inbox WHERE account=$a AND provider=$p
            AND (stamp,key)<($time,$key) ORDER BY stamp DESC,key DESC LIMIT $limit
            """);
        return Task.FromResult(ProtocolReceiveProvider.ReadInbox(cmd,a,before,limit));
    }
    public async Task<string> Open(MailAccount a,string key,CancellationToken token)
    {
        using var db = _store.Open();
        using var cmd = Command(db,a,"SELECT folder,item,raw FROM provider_inbox WHERE account=$a AND provider=$p AND key=$key");
        cmd.Parameters.AddWithValue("$key",key);
        string folder,item; byte[]? raw;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("邮件已不在此收件箱。");
            folder = reader.GetString(0); item = reader.GetString(1); raw = reader.IsDBNull(2) ? null : (byte[])reader[2];
        }
        if (raw == null)
        {
            var provider = Find(a) ?? throw new InvalidDataException("需要启用此邮箱插件。");
            raw = await BuiltinMailboxAdapters.ReadOriginal(provider,Identity(a),folder,item,token);
            var parsed = await new MimeReader().Parse(raw,token);
            cmd.CommandText = "UPDATE provider_inbox SET raw=$raw WHERE account=$a AND provider=$p AND key=$key";
            cmd.Parameters.AddWithValue("$raw",raw); cmd.ExecuteNonQuery(); return parsed.Text;
        }
        return (await new MimeReader().Parse(raw,token)).Text;
    }
}
