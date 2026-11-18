using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ProtocolReceiveProvider(SqliteMailStore store, ProtocolMailService protocols) : IReceiveProvider
{
    public bool Supports(MailAccount account) => account.Provider is "imap" or "imap-smtp" or "pop3" &&
        account.Capabilities.HasFlag(AccountCapabilities.Receive);

    public async Task Receive(MailAccount account, int range, CancellationToken token)
    {
        if (account.Provider == "pop3")
            await protocols.ReceivePop(account.Id, false, Math.Min(500, range), token);
        else
            await protocols.Sync(account.Id, "INBOX", range, cancellation: token);
    }

    public async Task<IReadOnlyList<InboxMessage>> Inbox(MailAccount account, InboxPosition? before, int limit, CancellationToken token)
    {
        if (account.Provider != "pop3")
        {
            var endpoint = (await protocols.Settings(account.Id, token)).Incoming;
            return endpoint is null ? [] : new ImapCache(store, account.Id, endpoint).Inbox(account, before, limit);
        }
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT CAST(unixepoch(m.date,'subsec')*1000 AS INTEGER) AS stamp,m.id,m.subject,m.sender,1
            FROM messages m JOIN folders f ON m.folder_id=f.id WHERE f.account_id=$account AND f.role='pop-inbox'
            AND (stamp,m.id)<($time,$key) ORDER BY stamp DESC,m.id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$account", account.Id.ToString());
        return ReadInbox(command, account, before, limit);
    }

    internal static IReadOnlyList<InboxMessage> ReadInbox(SqliteCommand command, MailAccount account, InboxPosition? before, int limit)
    {
        command.Parameters.AddWithValue("$time", before?.UnixMilliseconds ?? long.MaxValue);
        command.Parameters.AddWithValue("$key", before?.Key ?? "");
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var items = new List<InboxMessage>();
        while (reader.Read()) items.Add(new(account.Id, account.Name, new(reader.GetInt64(0), reader.GetString(1)),
            reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        return items;
    }

    public async Task<string> Open(MailAccount account, string key, CancellationToken token)
    {
        if (account.Provider != "pop3")
        {
            var endpoint = (await protocols.Settings(account.Id, token)).Incoming ?? throw new InvalidDataException("收信配置不存在。");
            var item = new ImapCache(store, account.Id, endpoint).FindInboxMessage(key);
            return (await protocols.OpenMessage(account.Id, "INBOX", item, true, token)).Text;
        }
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT m.raw FROM messages m JOIN folders f ON m.folder_id=f.id
            WHERE m.id=$key AND f.account_id=$account AND f.role='pop-inbox'
            """;
        command.Parameters.AddWithValue("$account", account.Id.ToString());
        command.Parameters.AddWithValue("$key", key);
        if (command.ExecuteScalar() is not byte[] raw) throw new InvalidDataException("邮件已不在此账户的收件箱。");
        var mail = await new MimeReader().Parse(raw, token);
        return string.IsNullOrEmpty(mail.Text) ? "请在本地邮箱中打开安全 HTML 或附件。" : mail.Text;
    }
}
