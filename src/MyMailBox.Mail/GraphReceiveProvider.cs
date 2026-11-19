using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class GraphReceiveProvider(SqliteMailStore store, GraphMailService graph) : IReceiveProvider
{
    public bool Supports(MailAccount account) => account.Provider == "outlook-com";

    public async Task Receive(MailAccount account, int range, CancellationToken token)
    {
        var folder = await graph.ResolveInbox(account.Id, token);
        await graph.SyncPages(account.Id, folder, range, token);
    }

    // The same ordering key identifies a message across pages, restarts and account display-name changes.
    private const string Rows = """
        SELECT COALESCE(CAST(unixepoch(i.date,'subsec')*1000 AS INTEGER),0) AS stamp,
               'graph/'||i.account||'/'||hex(i.home)||'/'||hex(i.id) AS position,
               json_extract(i.data,'$.Subject') AS subject,json_extract(i.data,'$.From') AS sender,
               m.id AS local,i.id AS remote,i.account AS account,i.home AS home
          FROM graph_items i JOIN graph_inboxes b ON b.account=i.account AND b.home=i.home AND b.folder=i.folder
          JOIN graph_settings s ON s.account_id=i.account AND s.home_id=i.home
          LEFT JOIN graph_receipts r ON r.account_id=i.account AND r.home_id=i.home AND r.remote_id=i.id
          LEFT JOIN messages m ON m.id=r.message_id
        """;

    public Task<IReadOnlyList<InboxMessage>> Inbox(MailAccount account, InboxPosition? before, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var db = store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT stamp,position,subject,sender,local IS NOT NULL FROM ({Rows}) WHERE account=$a AND home=$h AND (stamp,position)<($time,$key) ORDER BY stamp DESC,position DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$a", account.Id.ToString());
        cmd.Parameters.AddWithValue("$h", (object?)graph.CurrentHome(account.Id) ?? DBNull.Value);
        return Task.FromResult(ProtocolReceiveProvider.ReadInbox(cmd, account, before, limit));
    }

    public async Task<string> Open(MailAccount account, string key, CancellationToken token)
    {
        using var db = store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT remote FROM ({Rows}) WHERE account=$a AND home=$h AND position=$key";
        cmd.Parameters.AddWithValue("$a", account.Id.ToString());
        cmd.Parameters.AddWithValue("$h", (object?)graph.CurrentHome(account.Id) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", key);
        var remote = cmd.ExecuteScalar() as string ?? throw new InvalidDataException("邮件已不在此账户的收件箱。");
        var local = await graph.Download(account.Id, remote, token);
        var parsed = await new MimeReader().Parse(await store.Read(local, token), token);
        return parsed.Text.Length > 0 ? parsed.Text : "请在本地邮箱打开已下载的 HTML 正文或附件。";
    }

    internal static (Guid Account, Guid? Id) ReplySource(SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT account,local FROM ({Rows}) WHERE position=$key";
        cmd.Parameters.AddWithValue("$key", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException("邮件已不在此账户的收件箱。");
        if (r.IsDBNull(1)) throw new InvalidDataException("请先在统一收件箱打开并下载原邮件。");
        return (Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)));
    }
}

public sealed partial class GraphMailService
{
    internal string? CurrentHome(Guid account) => Configuration(account).Home;

    internal Task<GraphFolder> ResolveInbox(Guid account, CancellationToken token) => Run(account, async () =>
    {
        const string route = Root + "mailFolders/inbox";
        using var data = await Json(account, route + "?$select=id,displayName,childFolderCount", new Uri(route).AbsolutePath, token);
        var row = data.RootElement;
        var folder = new GraphFolder(Required(row, "id"), Required(row, "displayName"), row.GetProperty("childFolderCount").GetInt32());
        using var db = _store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO graph_inboxes VALUES($a,$h,$f) ON CONFLICT(account,home) DO UPDATE SET folder=$f";
        cmd.Parameters.AddWithValue("$a", account.ToString()); cmd.Parameters.AddWithValue("$h", CurrentHome(account)!); cmd.Parameters.AddWithValue("$f", folder.Id);
        token.ThrowIfCancellationRequested(); cmd.ExecuteNonQuery();
        return folder;
    }, token);
}
