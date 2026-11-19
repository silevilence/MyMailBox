using MailKit.Net.Smtp;
using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class ProtocolMailService
{
    private sealed class SendingClient : SmtpClient
    {
        public List<string> Accepted { get; } = [];
        public List<string> Rejected { get; } = [];
        protected override void OnRecipientAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) => Accepted.Add(mailbox.Address);
        protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) => Rejected.Add(mailbox.Address);
    }
    public async Task<SendOutcome> Send(Guid account, TextMail message, CancellationToken cancellation = default)
    {
        var id = Guid.NewGuid();
        await Enqueue(id, account, message, cancellation);
        return await Deliver(id, cancellation);
    }

    public Task Enqueue(Guid id, Guid account, TextMail message, CancellationToken cancellation = default) => Queue(id,account,message,null,cancellation);
    public Task Schedule(Guid id,Guid account,TextMail message,SendPlan plan,CancellationToken token=default) => Queue(id,account,message,plan,token);
    public Task<SendPlan?> GetSendPlan(Guid id,CancellationToken token=default) => Task.Run(()=>_outbox.Plan(id),token);
    private async Task Queue(Guid id,Guid account,TextMail message,SendPlan? plan,CancellationToken cancellation,string? automatic=null)
    {
        var identity = await SendingAccount(account, cancellation);
        var (raw, addresses) = await _draftStore.Outgoing(id, identity, message, automatic, false, cancellation);
        var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            automatic is null ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { identity.Address, message }) :
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { identity.Address, message, automatic })));
        if(plan is null)_outbox.Enqueue(id,account,message.Subject,raw,addresses,requestHash);
        else _outbox.Schedule(id,account,message.Subject,raw,addresses,requestHash,plan);
    }

    private static async Task<SendOutcome> Submit(SendingClient client, MimeMessage mime, string[] remaining, CancellationToken cancellation)
    {
        var recipients = remaining.Select(value => MailboxAddress.Parse(value)).ToArray();
        try
        {
            // Bcc recipients travel only in the SMTP envelope; never create a Bcc header.
            await client.SendAsync(mime, mime.From.Mailboxes.Single(), recipients, cancellation);
        }
        catch (SmtpCommandException error)
        {
            var description = error.ErrorCode == SmtpErrorCode.SenderNotAccepted ? "服务器拒绝此账户的发件身份，请核对发件地址和服务器许可。" : "服务器拒绝本次发送，邮件未获接收确认。请检查收件地址和服务器策略。";
            return new("rejected", description, [], client.Rejected.ToArray());
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or MailKit.ProtocolException or OperationCanceledException or TimeoutException)
        {
            return new("unknown", "发送中断，服务器是否接收无法确认。请先核对收件箱，避免重复发送；不会自动重试。", [], client.Rejected.ToArray());
        }
        return new(client.Rejected.Count == 0 ? "sent" : "partial", client.Rejected.Count == 0 ? "服务器已接收邮件。" : "部分收件人已获服务器接收，另有收件人被拒绝。请勿向全部收件人重复发送。", client.Accepted.ToArray(), client.Rejected.ToArray());
    }
}
