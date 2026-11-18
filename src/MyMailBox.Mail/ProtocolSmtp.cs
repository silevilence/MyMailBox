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

    public async Task Enqueue(Guid id, Guid account, TextMail message, CancellationToken cancellation = default)
    {
        var identity = await Account(account, AccountCapabilities.Send, cancellation);
        using var mime = new MimeMessage();
        var profile=_draftStore.Profile(account);
        var from=message.FromIdentity ?? identity.Address;
        if(!profile.Aliases.Append(identity.Address).Contains(from,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("所选身份不属于当前发送账户。");
        mime.From.Add(MailboxAddress.Parse(from));
        void Addresses(string value, InternetAddressList destination)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.Contains('\r') || value.Contains('\n') || !InternetAddressList.TryParse(value, out var addresses) || addresses.Count == 0 || addresses.Any(a => a is not MailboxAddress) || addresses.Mailboxes.Any(a => !a.Address.Contains('@')))
                throw new InvalidDataException("收件地址无效，请填写完整邮箱地址，多个地址用逗号分隔。");
            destination.AddRange(addresses);
        }
        Addresses(message.To, mime.To); Addresses(message.Cc, mime.Cc);
        var blind = new InternetAddressList(); Addresses(message.Bcc, blind);
        var recipients = mime.To.Mailboxes.Concat(mime.Cc.Mailboxes).Concat(blind.Mailboxes).DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase).ToArray();
        if (recipients.Length == 0) throw new InvalidDataException("请至少填写一位收件人。");
        if (message.Subject.Contains('\r') || message.Subject.Contains('\n')) throw new InvalidDataException("主题不能包含换行。");
        mime.Subject = message.Subject; mime.Body = _draftStore.Body(account,message,cancellation);
        mime.Date = DateTimeOffset.Now; mime.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        using var stream = new MemoryStream();
        await mime.WriteToAsync(stream, cancellation);
        if (stream.Length > profile.MaxBytes) throw new InvalidDataException($"编码后的整封邮件超过当前账户上限 {profile.MaxBytes:N0} 字节。");
        var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { identity.Address, message })));
        _outbox.Enqueue(id, account, mime.Subject, stream.ToArray(), recipients.Select(r => r.Address).ToArray(), requestHash);
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
