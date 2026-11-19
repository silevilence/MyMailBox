using System.Net;
using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class GraphMailService
{
    private sealed class DeltaExpired : Exception;
    private sealed class DeltaFolderMissing : Exception;

    private async Task<GraphDeltaStore> Delta(Guid account, CancellationToken token)
    {
        await Account(account, token);
        return new(_store, account, Configuration(account).Home ?? throw new InvalidDataException("请先登录对应微软账户。"));
    }
    public async Task<IReadOnlyList<GraphSyncScope>> Scopes(Guid account, CancellationToken token) =>
        (await Delta(account, token)).Scopes();

    public async Task<GraphOfflinePage> Cached(Guid account, string folder, int offset, CancellationToken token) =>
        (await Delta(account, token)).Cached(folder, offset);

    public Task<GraphSyncScope> Sync(Guid account, GraphFolder folder, CancellationToken token) =>
        SyncPages(account, folder, null, token);

    internal Task<GraphSyncScope> SyncPages(Guid account, GraphFolder folder, int? remaining, CancellationToken token) => Run(account, async () =>
    {
        var store = await Delta(account, token);
        var state = store.Begin(folder);
        var route = Root + "mailFolders/" + Id(folder.Id) + "/messages/delta";
        var initial = route + "?$select=id,subject,from,receivedDateTime,isRead,hasAttachments";
        var reset = false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var uri = DeltaLink(state.Link ?? initial, folder.Id);
            if (!visited.Add(uri.AbsoluteUri)) throw new InvalidDataException("微软重复返回同一分页链接，已暂停同步。");
            byte[] raw;
            try { raw = await Get(account, uri, 4 * 1024 * 1024, token, delta: true, pageSize: remaining); }
            catch (DeltaExpired)
            {
                if (reset || state.Link == null) throw new InvalidDataException("微软未能建立新的同步游标，保留已有邮件。");
                state = store.Reset(folder.Id, state); visited.Clear(); reset = true;
                continue;
            }
            catch (DeltaFolderMissing)
            {
                store.Remove(folder.Id, state);
                throw new InvalidDataException("远程目录已不存在，已移除此目录的同步范围；已下载的独立归档仍可阅读。");
            }
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var next = root.TryGetProperty("@odata.nextLink", out var nextValue) ? nextValue.GetString() : null;
            var final = next == null;
            var link = next ?? (root.TryGetProperty("@odata.deltaLink", out var last) ? last.GetString() : null);
            if (link == null) throw new InvalidDataException("微软未返回有效的增量进度。");
            var checkedLink = DeltaLink(link, folder.Id).AbsoluteUri;
            if (!final && visited.Contains(checkedLink)) throw new InvalidDataException("微软重复返回同一分页链接，已暂停同步。");
            var items = root.GetProperty("value");
            state = store.Apply(folder.Id, state, items, checkedLink, final, token);
            remaining -= items.GetArrayLength();
            if (final || remaining <= 0) return store.Scopes().Single(s => s.Id == folder.Id);
        }
    }, token);

    // Graph may return either segment-style or OData key-style links for the same folder.
    private static Uri DeltaLink(string link, string folder)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) throw new InvalidDataException("无效的微软分页链接。");
        var collection = "/v1.0/me/mailFolders";
        var suffix = "/messages/delta";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(collection, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || path.Length <= collection.Length + suffix.Length)
            throw new InvalidDataException("分页链接不属于当前同步目录。");
        var key = Uri.UnescapeDataString(path[collection.Length..^suffix.Length]);
        if (key != "/" + folder && key != "('" + folder.Replace("'", "''") + "')")
            throw new InvalidDataException("分页链接不属于当前同步目录。");
        return ValidatePage(link, path);
    }

    private static async Task<bool> Expired(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.Gone) return true;
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound)) return false;
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8193];
        int length = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: token);
        if (length == 0 || length == buffer.Length) return false;
        try
        {
            using var error = JsonDocument.Parse(buffer.AsMemory(0, length));
            return error.RootElement.TryGetProperty("error", out var details) && details.TryGetProperty("code", out var code)
                && string.Equals(code.GetString(), "syncStateNotFound", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }
}
