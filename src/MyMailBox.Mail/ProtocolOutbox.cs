using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using System.Net.Sockets;

namespace MyMailBox.Mail;

public sealed partial class ProtocolMailService
{
    private readonly OutboxStore _outbox;

    public Task<IReadOnlyList<OutboxItem>> List(Guid account, int offset, int limit, CancellationToken cancellation = default) =>
        Task.Run(() =>
        {
            if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            return _outbox.List(account, offset, limit);
        }, cancellation);

    public Task<IReadOnlyList<string>> History(Guid id, CancellationToken cancellation = default) =>
        Task.Run(() => _outbox.History(id), cancellation);
    public Task Cancel(Guid id, CancellationToken cancellation = default) => Task.Run(() => _outbox.Cancel(id), cancellation);
    public Task Retry(Guid id, CancellationToken cancellation = default) => Task.Run(() => _outbox.Retry(id), cancellation);

    public async Task SaveCopy(Guid id, CancellationToken cancellation = default)
    {
        var item = _outbox.Get(id);
        if (!item.CopyPending) return;
        var raw = _outbox.Raw(id);
        var parsed = await new MimeReader().Parse(raw, cancellation);
        _outbox.SaveCopy(item, raw, parsed, cancellation);
    }

    private static SendOutcome Outcome(OutboxItem item) => new(item.State,
        item.Detail + (item.CopyPending ? " 已获接收的邮件尚未保存本地副本，可在发件箱单独补存。" : ""),
        item.Accepted, item.State is "partial" or "rejected" ? item.Remaining : []);

    private static SendOutcome CheckReceipt(SendOutcome receipt, string[] envelope)
    {
        var accepted = receipt.Accepted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejected = receipt.Rejected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool valid = receipt.State switch {
            "sent" => accepted.SetEquals(envelope) && rejected.Count == 0,
            "rejected" => accepted.Count == 0 && rejected.SetEquals(envelope),
            "partial" => accepted.Count > 0 && rejected.Count > 0 && !accepted.Overlaps(rejected) &&
                envelope.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(accepted.Concat(rejected)),
            "unknown" => accepted.Count == 0 && rejected.Count == 0,
            _ => false
        };
        return valid ? receipt : new("unknown", "邮箱插件的回执与收件人不一致，请核对投递状态。", [], []);
    }

    internal Guid? NextDelivery() => _outbox.Next();

    // The OS releases this lock on process exit. Only its current owner may recover an interrupted SMTP submission.
    private FileStream? LockDispatch()
    {
        try { return new FileStream(_store.DatabasePath + ".outbox.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }

    public async Task<SendOutcome> Deliver(Guid id, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var dispatch = LockDispatch();
        if (dispatch is null) return Outcome(_outbox.Get(id));
        _outbox.RecoverInterrupted();
        _outbox.ActivateDue(id);
        var item = _outbox.Get(id);
        if (item.State != "pending") return Outcome(item);
        var raw = _outbox.Raw(id);
        using var mime = await MimeMessage.LoadAsync(new MemoryStream(raw, false), cancellation);
        SendOutcome result;
        using var client = new SendingClient();
        try
        {
            var account = await SendingAccount(item.Account, cancellation);
            if (!_draftStore.Profile(account.Id).Aliases.Append(account.Address).Contains(mime.From.Mailboxes.Single().Address,StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("发信身份已变更，请核对账户和已排队的邮件。");
            if (account.Provider == "outlook-com")
            {
                result = await Graph!.SendQueued(account.Id, raw, item.Remaining, () => _outbox.Claim(id), cancellation);
            }
            else if (account.Provider.Contains('/') || account.Provider=="exchange-eas")
            {
                var provider = ExternalSender(account.Provider) ?? throw new InvalidDataException("所需邮箱插件已停用。");
                if (!_outbox.Claim(id)) return Outcome(_outbox.Get(id));
                try
                {
                    using var stream = new MemoryStream(raw, false);
                    result = CheckReceipt(await provider.Send(new(account.Id, account.Address), id, stream, item.Remaining, cancellation), item.Remaining);
                }
                catch (Exception) { result = new("unknown", "邮箱插件未返回发送回执，请先核对投递状态。", [], []); }
            }
            else
            {
                await Connect(client, item.Account, true, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!_outbox.Claim(id)) return Outcome(_outbox.Get(id));
                result = await Submit(client, mime, item.Remaining, cancellation);
            }
        }
        catch (AuthenticationException)
        {
            _outbox.BeforeSubmissionFailure(id, false, "认证失败，邮件尚未提交。请更新发信凭据后重试。");
            await _accounts.AuthenticationFailed(item.Account);
            return Outcome(_outbox.Get(id));
        }
        catch (Exception e) when (e is SslHandshakeException or NotSupportedException or InvalidDataException)
        {
            _outbox.BeforeSubmissionFailure(id, false, e is InvalidDataException ? e.Message : "发信连接安全配置无效，邮件尚未提交。请检查服务器和证书。");
            return Outcome(_outbox.Get(id));
        }
        catch (Exception e) when (e is SocketException or IOException or TimeoutException or MailKit.ProtocolException or MailKit.CommandException)
        {
            _outbox.BeforeSubmissionFailure(id, true, "未连接到发信服务器，邮件仍在发件箱。一分钟后重试，也可以取消。");
            return Outcome(_outbox.Get(id));
        }
        _outbox.Record(item, result);
        if (result.Accepted.Count > 0)
        {
            _draftStore.Remember(item.Account,result.Accepted,mime);
            try { await SaveCopy(id, CancellationToken.None); }
            catch (Exception e) when (e is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or FormatException)
            { /* Acceptance is durable. The copy_pending flag exposes a local-only retry. */ }
        }
        return Outcome(_outbox.Get(id));
    }
}

public sealed class OutboxRunner : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public OutboxRunner(ProtocolMailService service, Action<string>? notify = null)
    {
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            try
            {
                do
                {
                    try
                    {
                        if (service.NextDelivery() is Guid id) await service.Deliver(id, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception) { notify?.Invoke("发件箱处理未完成，请查看发件记录并检查本地存储。"); }
                } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }
    public void Dispose() { _stop.Cancel(); _loop.GetAwaiter().GetResult(); _stop.Dispose(); }
}
