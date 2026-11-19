using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailDraftStore
{
    internal async Task<(byte[] Raw, string[] Recipients)> Outgoing(Guid id, MailAccount identity, TextMail message, string? automatic, bool draft, CancellationToken cancellation)
    {
        using var mime = new MimeMessage();
        var profile=Profile(identity.Id);
        var from=message.FromIdentity ?? identity.Address;
        if(!profile.Aliases.Append(identity.Address).Contains(from,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("所选身份不属于当前发送账户。");
        mime.From.Add(MailboxAddress.Parse(from));
        var fields=Normalize(new(message.To,message.Cc,message.Bcc));
        if(fields.To.Length>0)mime.To.AddRange(InternetAddressList.Parse(fields.To));
        if(fields.Cc.Length>0)mime.Cc.AddRange(InternetAddressList.Parse(fields.Cc));
        var blind=fields.Bcc.Length==0 ? new InternetAddressList() : InternetAddressList.Parse(fields.Bcc);
        var recipients = mime.To.Mailboxes.Concat(mime.Cc.Mailboxes).Concat(blind.Mailboxes).DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase).ToArray();
        if (identity.Provider == "outlook-com") mime.Bcc.AddRange(blind);
        if (!draft && recipients.Length == 0) throw new InvalidDataException("请至少填写一位收件人。");
        if (message.Subject.Contains('\r') || message.Subject.Contains('\n')) throw new InvalidDataException("主题不能包含换行。");
        mime.Subject = message.Subject; mime.Body = Body(identity.Id,message,cancellation);
        if(message.InReplyTo is {Length:>0} parent)mime.InReplyTo=parent;
        foreach(var reference in message.References.Distinct(StringComparer.Ordinal))mime.References.Add(reference);
        if (automatic is not null)
        {
            mime.Headers.Add("Auto-Submitted", automatic);
            mime.Headers.Add("X-MyMailBox-Automation", id.ToString("D"));
            mime.Headers.Add("X-Auto-Response-Suppress","All");
        }
        mime.Date = DateTimeOffset.Now; mime.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        using var stream = new MemoryStream();
        var format = FormatOptions.Default.Clone();
        format.NewLineFormat = NewLineFormat.Dos;
        await mime.WriteToAsync(format, stream, cancellation);
        if (stream.Length > profile.MaxBytes) throw new InvalidDataException($"编码后的整封邮件超过当前账户上限 {profile.MaxBytes:N0} 字节。");
        if (identity.Provider == "outlook-com") GraphMailService.CheckMimeSize(stream.Length);
        return (stream.ToArray(), recipients.Select(r => r.Address).ToArray());
    }
}
