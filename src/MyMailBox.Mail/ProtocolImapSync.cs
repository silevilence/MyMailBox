using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class ProtocolMailService
{
    private async Task<ImapCache?> OfflineCache(Guid account, CancellationToken token)
    {
        await Account(account, AccountCapabilities.Receive | AccountCapabilities.Folders, token);
        var endpoint = (await Settings(account, token)).Incoming;
        return endpoint is null ? null : new ImapCache(_store, account, endpoint);
    }

    public async Task<IReadOnlyList<string>> CachedFolders(Guid account, CancellationToken cancellation = default) =>
        (await OfflineCache(account, cancellation))?.Paths() ?? [];

    public async Task<ImapCachePage> CachedPage(Guid account, string path, int offset, int limit, CancellationToken cancellation = default)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        return (await OfflineCache(account, cancellation))?.Page(path, offset, limit) ?? new([], false);
    }

    public Task<ImapSyncResult> Sync(Guid account, string path, int recentCount, IProgress<int>? progress = null, CancellationToken cancellation = default) =>
        ReadOperation(account, async () =>
        {
            if (recentCount is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(recentCount));
            var identity = await Account(account, AccountCapabilities.Receive | AccountCapabilities.Folders, cancellation);
            using var client = _imapClients();
            var endpoint = await Connect(client, account, false, cancellation);
            var cache = new ImapCache(_store, account, endpoint);
            IMailFolder remote;
            try
            {
                remote = await client.GetFolderAsync(path, cancellation);
                await remote.OpenAsync(FolderAccess.ReadOnly, cancellation);
            }
            catch (FolderNotFoundException)
            {
                cache.Detach(path);
                throw new InvalidDataException("服务器目录已改名或移除，请刷新目录。旧引用不会用于新目录。");
            }
            var folder = cache.Start(path, remote.UidValidity, identity.Name);
            var count = remote.Count;
            var next = remote.UidNext?.Id;
            // Take a fixed UID snapshot before paging. Sequence numbers can change after an EXPUNGE.
            var cursorValid = folder.Watermark > 0 && (next is null || next > folder.Watermark);
            bool incremental = cursorValid && recentCount <= folder.Coverage;
            uint watermark = cursorValid ? folder.Watermark : 0;
            int added = 0;
            if (count > 0 && !(incremental && next == (long)watermark + 1))
            {
                var firstIndex = Math.Max(0, count - recentCount);
                var first = (await remote.FetchAsync(firstIndex, firstIndex, MessageSummaryItems.UniqueId, cancellation))
                    .FirstOrDefault(s => s.Index == firstIndex);
                if (first is null) throw new InvalidDataException("目录在同步中发生变化，请重新同步。");
                var low = incremental ? Math.Max((long)first.UniqueId.Id, (long)watermark + 1) : first.UniqueId.Id;
                if (low <= uint.MaxValue && (next is null || low < next))
                {
                    var range = new UniqueIdRange(new((uint)low), next is > 1 ? new(next.Value - 1) : UniqueId.MaxValue);
                    var uids = await remote.SearchAsync(SearchQuery.Uids(range), cancellation);
                    foreach (var batch in uids.Where(u => u.Id >= low && (next is null || u.Id < next)).OrderByDescending(u => u.Id).Chunk(32))
                    {
                        var requested = cache.MissingSummaries(folder, batch);
                        if (requested.Count > 0)
                        {
                            var summaries = await remote.FetchAsync(requested.ToArray(), MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Size | MessageSummaryItems.Flags, cancellation);
                            added += cache.Add(folder, summaries.Where(s => requested.Contains(s.UniqueId)), cancellation);
                        }
                        watermark = Math.Max(watermark, batch[0].Id);
                        progress?.Report(added);
                    }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            // A completed empty range can include expunged UIDs. Never advance over a canceled batch.
            await ReconcileFlags(remote,cache,folder,recentCount,cancellation);
            cache.Finish(folder, next is > 0 ? next.Value - 1 : watermark, recentCount);
            return new ImapSyncResult(added, remote.UidValidity, next, count);
        });

    public async Task WaitForChange(Guid account, string path, ImapSyncResult previous, CancellationToken cancellation = default)
    {
        await ReadOperation(account, async () =>
        {
            await Account(account, AccountCapabilities.Receive | AccountCapabilities.Folders, cancellation);
            using var client = _imapClients();
            await Connect(client, account, false, cancellation);
            var remote = await client.GetFolderAsync(path, cancellation);
            await remote.OpenAsync(FolderAccess.ReadOnly, cancellation);
            if (remote.Count != previous.Count || remote.UidValidity != previous.Validity || remote.UidNext?.Id != previous.NextUid) return true;
            if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
            {
                using var done = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                void Changed(object? sender, EventArgs args) => done.Cancel();
                remote.CountChanged += Changed;
                remote.MessageFlagsChanged += Changed;
                try { await client.IdleAsync(done.Token, cancellation); }
                finally { remote.CountChanged -= Changed; remote.MessageFlagsChanged -= Changed; }
            }
            else
            {
                await client.DisconnectAsync(true, cancellation);
                await Task.Delay(TimeSpan.FromMinutes(1), cancellation);
            }
            return true;
        });
    }

    public Task<ImapReading> OpenMessage(Guid account, string path, ImapCachedMail message, bool allowDownload, CancellationToken cancellation = default) =>
        ReadOperation(account, async () =>
        {
            var cache = await OfflineCache(account, cancellation) ?? throw new InvalidDataException("请先保存收信配置。");
            if (cache.LocalId(path, message) is Guid known)
            {
                var local = await new MimeReader().Parse(await _store.Read(known, cancellation), cancellation);
                return Reading(known, local);
            }
            if (!allowDownload) throw new InvalidDataException("正文尚未缓存，联网下载后可离线阅读。");
            using var client = _imapClients();
            var endpoint = await Connect(client, account, false, cancellation);
            cache = new ImapCache(_store, account, endpoint);
            cache.LocalId(path, message);
            var remote = await client.GetFolderAsync(path, cancellation);
            await remote.OpenAsync(FolderAccess.ReadOnly, cancellation);
            if (remote.UidValidity != message.Validity) throw new InvalidDataException("服务器目录标识已变更，请重新同步。");
            var uid = new UniqueId(message.Uid);
            var info = (await remote.FetchAsync(new[] { uid }, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, cancellation)).FirstOrDefault(s => s.UniqueId == uid);
            if (info is null)
            {
                cache.MarkMissing(message);
                throw new InvalidDataException("服务器上已不存在这封邮件。");
            }
            if (info.Size > MimeReader.MaxMessageBytes) throw new InvalidDataException("邮件超过 25 MiB 离线阅读限制。");
            byte[] raw;
            try
            {
                using var stream = await remote.GetStreamAsync(uid, 0, MimeReader.MaxMessageBytes + 1, cancellation);
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes, cancellation);
                if (bytes.Length > MimeReader.MaxMessageBytes) throw new InvalidDataException("邮件超过 25 MiB 离线阅读限制。");
                raw = bytes.ToArray();
            }
            catch (MessageNotFoundException)
            {
                cache.MarkMissing(message);
                throw new InvalidDataException("服务器上已不存在这封邮件。");
            }
            var parsed = await new MimeReader().Parse(raw, cancellation);
            return Reading(cache.Save(path, message, raw, parsed, cancellation), parsed);
        });

    private static ImapReading Reading(Guid id, ParsedMail parsed) =>
        new(parsed.Subject, parsed.Text.Length == 0 ? "请在本地邮箱打开安全 HTML 或查看附件。" : parsed.Text, id);
}
