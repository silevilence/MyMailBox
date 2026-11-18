using MailKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class ProtocolMailService
{
    public async Task Queue(Guid account, string path, ImapCachedMail message, MailMark mark, CancellationToken token = default)
    {
        var cache = await OfflineCache(account, token) ?? throw new InvalidDataException("此账户没有缓存。");
        cache.Queue(path, message, mark);
    }

    public async Task<IReadOnlyList<OfflineMark>> Changes(Guid account, CancellationToken token = default) =>
        (await OfflineCache(account, token))?.Edits() ?? [];

    public async Task Dismiss(Guid account, Guid edit, CancellationToken token = default) =>
        (await OfflineCache(account, token))?.Dismiss(edit);

    private static async Task ReconcileFlags(IMailFolder remote, ImapCache cache, ImapCache.Folder folder, int range, CancellationToken token)
    {
        const MessageSummaryItems fields = MessageSummaryItems.UniqueId | MessageSummaryItems.Flags;
        foreach (var batch in cache.Observed(folder, range).Chunk(128))
        {
            var observed = await remote.FetchAsync(batch, fields, token);
            cache.Snapshot(folder.Id, batch, observed);
        }
        var pending = cache.Edits(folder.Id);
        if (pending.Count == 0) return;
        await remote.OpenAsync(FolderAccess.ReadWrite, token);
        if (remote.UidValidity != folder.Validity) throw new InvalidDataException("目录已重新编号，请重新同步，旧标识不会用于修改邮件。");
        foreach (var edit in pending)
        {
            var uid = new UniqueId(edit.Uid);
            var flag = edit.Mark is MailMark.Read or MailMark.Unread ? MessageFlags.Seen : MessageFlags.Flagged;
            var desired = edit.Mark is MailMark.Read or MailMark.Star;
            if (remote.Access != FolderAccess.ReadWrite || !remote.PermanentFlags.HasFlag(flag))
            {
                cache.Result(edit, "conflict", "服务器未授予此标记的写入能力。");
                continue;
            }
            // Add/remove one bit only. Independent flag edits by other clients survive.
            if (desired) await remote.AddFlagsAsync(uid, flag, true, token);
            else await remote.RemoveFlagsAsync(uid, flag, true, token);
            var result = await remote.FetchAsync(new[] { uid }, fields, token);
            cache.Snapshot(folder.Id, [uid], result);
            var s = result.FirstOrDefault(m => m.UniqueId == uid);
            if (s?.Flags is { } flags)
                cache.Result(edit, flags.HasFlag(flag) == desired ? "done" : "conflict",
                    flags.HasFlag(flag) == desired ? "服务器已确认离线修改。" : "服务器状态未匹配，请核对后重新选择。");
        }
    }
}
