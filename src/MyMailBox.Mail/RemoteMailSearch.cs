using MailKit;
using MailKit.Search;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailSearchService
{
    public async Task<RemoteSearchPage> SearchRemote(SavedSearch definition, IProgress<SearchIndexProgress>? progress = null, CancellationToken token = default)
    {
        if (definition.Account is not Guid account || definition.Folder is not Guid folder)
            return new(null, false, "请选择已同步的 IMAP 目录以搜索服务器。");
        var terms = string.IsNullOrWhiteSpace(definition.Expression) ? SearchQuery.All : RemoteQuery(ConditionTree.Parse(definition.Expression).Root);
        if (terms is null) return new(null, false, "服务器无法执行这组条件，仅显示本地结果。");
        var batch = Guid.NewGuid();
        try { return await _protocols.FindRemote(account, folder, terms, batch, progress, token); }
        catch (InvalidDataException e) { return new(batch, false, e.Message + " 已保留本地和已接收的结果。"); }
    }

    private static SearchQuery? RemoteQuery(ConditionNode node)
    {
        if (node.Criterion is { Match: SearchMatch.Contains } c && c.Value.Length > 0)
            return c.Field switch { "body" => SearchQuery.BodyContains(c.Value), "subject" => SearchQuery.SubjectContains(c.Value),
                "from" => SearchQuery.FromContains(c.Value), "to" => SearchQuery.ToContains(c.Value), "cc" => SearchQuery.CcContains(c.Value), _ => null };
        // Server NOT can include messages with unavailable fields, unlike our three-valued condition.
        if (node.Logic is not (ConditionLogic.And or ConditionLogic.Or)) return null;
        SearchQuery? result = null;
        foreach (var child in node.Children)
        {
            var query = RemoteQuery(child); if (query is null) return null;
            result = result is null ? query : node.Logic == ConditionLogic.And ? result.And(query) : result.Or(query);
        }
        return result;
    }
}

public sealed partial class ProtocolMailService
{
    internal Task<RemoteSearchPage> FindRemote(Guid account, Guid folder, SearchQuery query, Guid batch,
        IProgress<SearchIndexProgress>? progress, CancellationToken token) => ReadOperation(account, async () =>
    {
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path,validity FROM imap_folders WHERE id=$folder AND account=$account";
        cmd.Parameters.AddWithValue("$folder", folder.ToString()); cmd.Parameters.AddWithValue("$account", account.ToString());
        string path; uint validity;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return new RemoteSearchPage(null, false, "所选范围没有 IMAP 服务器搜索能力，仅显示本地结果。");
            path = r.GetString(0); validity = (uint)r.GetInt64(1);
        }
        var identity = await Account(account, AccountCapabilities.Receive | AccountCapabilities.Folders, token);
        using var client = _imapClients(); var endpoint = await Connect(client, account, false, token);
        var cache = new ImapCache(_store, account, endpoint);
        var binding = cache.Remote(folder);
        if (binding != (path,validity)) throw new InvalidDataException("目录标识已变更，请重新同步。");
        var remote = await client.GetFolderAsync(path, token); await remote.OpenAsync(FolderAccess.ReadOnly, token);
        if (remote.UidValidity != validity) throw new InvalidDataException("目录标识已变更，请重新同步。");
        var state = cache.Start(path,validity,identity.Name);
        int total = remote.Count, scanned = 0;
        var next = remote.UidNext; bool changed = false;
        remote.MessageExpunged += (_,_) => changed = true;
        cmd.CommandText = "DELETE FROM remote_search_hit WHERE created < unixepoch()-86400"; cmd.ExecuteNonQuery();
        for (int end = total - 1; end >= 0; end -= 64)
        {
            token.ThrowIfCancellationRequested();
            int first = Math.Max(0, end - 63);
            var window = await remote.FetchAsync(first,end,MessageSummaryItems.UniqueId,token);
            var uids = window.Select(m => m.UniqueId).ToArray();
            if (uids.Length > 0)
            {
                var matches = await remote.SearchAsync(SearchQuery.Uids(uids).And(query),token);
                if (matches.Count > 0)
                {
                    var summaries = await remote.FetchAsync(matches, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                        MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.Flags, token);
                    cache.Add(state, summaries, token);
                    using var tx = db.BeginTransaction(); using var insert = db.CreateCommand(); insert.Transaction = tx;
                    insert.CommandText = "INSERT OR IGNORE INTO remote_search_hit VALUES($batch,$id,unixepoch())";
                    insert.Parameters.AddWithValue("$batch", batch.ToString()); var id = insert.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
                    foreach (var m in summaries)
                    {
                        if (!matches.Contains(m.UniqueId)) continue;
                        id.Value = folder + "/" + m.UniqueId.Id; insert.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            scanned += window.Count; progress?.Report(new(scanned,total));
            if (changed) break;
        }
        var complete = !changed && remote.Count == total && remote.UidNext == next;
        return new RemoteSearchPage(batch, complete, complete ? "所选 IMAP 目录已搜索，服务器与本地结果按目录和 UID 合并。" : "搜索期间目录有变化，已显示部分结果；重新搜索可刷新。");
    });
}
