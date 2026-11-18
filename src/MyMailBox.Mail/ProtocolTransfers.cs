using MailKit;
using MyMailBox.Contracts;
using System.Security.Cryptography;

namespace MyMailBox.Mail;

public sealed partial class ProtocolMailService
{
    public async Task<IReadOnlyList<MyMailBox.Contracts.MailFolder>> Destinations(Guid sourceAccount, CancellationToken token = default)
    {
        var local = (await _accounts.List(token)).Where(a => a.Id != sourceAccount && a.Enabled && a.Provider == "local")
            .Select(a => a.Id).ToHashSet();
        return (await _store.Folders(token)).Where(f => local.Contains(f.AccountId)).ToArray();
    }

    public async Task<MailTransfer> Plan(TransferRequest request, CancellationToken token = default)
    {
        var source = await ManagedAccount(request.SourceAccount, token);
        if (source.Provider == "pop3" && request.Move) throw new InvalidDataException("POP3 下载副本可复制到本地。此处不提供服务器逐封移动。");
        var from = await LocalFolder(source.Id, request.SourceFolder, token);
        var to = (await Destinations(source.Id, token)).SingleOrDefault(f => f.Id == request.Destination)
            ?? throw new InvalidDataException("请选择其他已打开本地邮箱的目录。远端目标尚未提供可验证的导入能力。");
        _transfers.Plan(request, from.Path, to.Path);
        var planned = _transfers.Get(request.Id);
        if (planned.Request != request) throw new InvalidDataException("此操作标识已对应其他邮件或目录。");
        return planned;
    }

    public Task<IReadOnlyList<MailTransfer>> Recent(Guid sourceAccount, int offset, int limit, CancellationToken token = default) =>
        Task.Run(() =>
        {
            if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            return _transfers.Recent(sourceAccount, offset, limit);
        }, token);

    private FileStream LockTransfers()
    {
        try { return new FileStream(_store.DatabasePath + ".transfer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { throw new InvalidDataException("另一个归档操作正在运行，请稍后继续。"); }
    }

    public async Task<MailTransfer> Continue(Guid operation, IProgress<MailTransfer>? progress = null, CancellationToken token = default)
    {
        using var lease = LockTransfers();
        var step = _transfers.Get(operation);
        var request = step.Request;
        if (step.Phase is "done" or "canceled") return step;
        try
        {
            var account = await ManagedAccount(request.SourceAccount, token);
            if (step.Phase == "planned")
            {
                var content = IsImap(account) ? await ReadTransferSource(request, token) : _transfers.ReadLocal(request);
                var parsed = await new MimeReader().Parse(content.Raw, token);
                _transfers.SaveTarget(step, content, parsed, token);
                step = _transfers.Get(operation); progress?.Report(step);
            }
            token.ThrowIfCancellationRequested();
            if (step.Phase == "stored")
            {
                _transfers.VerifyTarget(step);
                _transfers.Update(operation, "verified", "目标原文和附件已按 SHA-256 验证。");
                step = _transfers.Get(operation); progress?.Report(step);
            }
            token.ThrowIfCancellationRequested();
            if (step.Phase == "verified")
            {
                _transfers.Update(operation, request.Move ? "cleaning" : "done", request.Move ? "目标已保存，准备核对并清理来源。" : "复制完成，来源原样保留。");
                step = _transfers.Get(operation); progress?.Report(step);
            }
            if (step.Phase == "cleaning")
            {
                token.ThrowIfCancellationRequested();
                _transfers.VerifyTarget(step);
                if (IsImap(account)) await FinishRemoteMove(step, token);
                else _transfers.FinishLocalMove(step, token);
                step = _transfers.Get(operation); progress?.Report(step);
            }
            return step;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is InvalidDataException or IOException or Microsoft.Data.Sqlite.SqliteException or FormatException)
        {
            var current = _transfers.Get(operation);
            var reason = e is InvalidDataException ? e.Message : "读取或写入失败，请检查本地存储和连接。";
            _transfers.Update(operation, current.Phase, reason);
            return _transfers.Get(operation);
        }
    }

    private async Task<TransferContent> ReadTransferSource(TransferRequest request, CancellationToken token) =>
        await OnImap(request.SourceAccount, async (client, cache) =>
        {
            var remote = await VerifiedFolder(client, cache, request.SourceFolder, FolderAccess.ReadOnly, token);
            return await ReadTransferContent(remote, request, token) ?? throw new InvalidDataException("来源邮件已经不存在。");
        }, token);

    private async Task<TransferContent?> ReadTransferContent(IMailFolder folder, TransferRequest request, CancellationToken token)
    {
        if (!uint.TryParse(request.Key, out var value) || value == 0) throw new InvalidDataException("来源 UID 无效。");
        var uid = new UniqueId(value);
        var row = (await folder.FetchAsync(new[] { uid }, MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Flags, token))
            .FirstOrDefault(m => m.UniqueId == uid);
        if (row is null) return null;
        if (row.Size > MimeReader.MaxMessageBytes) throw new InvalidDataException("原文超过本地 25 MiB 限制，已保留来源。");
        using var stream = await folder.GetStreamAsync(uid, 0, MimeReader.MaxMessageBytes + 1, token);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, token);
        if (buffer.Length > MimeReader.MaxMessageBytes || row.Size.HasValue && row.Size != buffer.Length)
            throw new InvalidDataException("下载原文的长度未通过核验，已保留来源。");
        return new(buffer.ToArray(), row.Flags?.HasFlag(MessageFlags.Seen) == true, row.Flags?.HasFlag(MessageFlags.Flagged) == true,
            _organization.Labels(request.SourceFolder, request.Key));
    }

    private async Task FinishRemoteMove(MailTransfer step, CancellationToken token)
    {
        var request = step.Request;
        await OnImap(request.SourceAccount, async (client, cache) =>
        {
            var remote = await VerifiedFolder(client, cache, request.SourceFolder, FolderAccess.ReadWrite, token);
            var current = await ReadTransferContent(remote, request, token);
            if (current is not null)
            {
                if (!client.Capabilities.HasFlag(MailKit.Net.Imap.ImapCapabilities.UidPlus))
                    throw new InvalidDataException("目标已保存。服务器不支持定向清除，请核对后手动整理来源。");
                if (Convert.ToHexString(SHA256.HashData(current.Raw)) != step.Sha256)
                    throw new InvalidDataException("来源原文与已验证副本不同，已保留两者。");
                var uid = new UniqueId(uint.Parse(request.Key));
                await remote.AddFlagsAsync(uid, MessageFlags.Deleted, true, token);
                await remote.ExpungeAsync(new[] { uid }, token);
            }
            RecordServerAction(request.SourceFolder, request.Key, null, null, MailAction.DeletePermanently);
            _transfers.Update(request.Id, "done", current is null ? "目标已验证；原 UID 已不存在。" : "目标已验证，已定向清除服务器来源。");
            return true;
        }, token);
    }
}
