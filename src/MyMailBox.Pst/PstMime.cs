using System.Text;
using MimeKit;
using XstReader;

namespace MyMailBox.Pst;

internal static class PstMime
{
    public static object Property(Message item, ushort tag) =>
        item.Properties.FirstOrDefault(p => (ushort)p.Tag == tag)?.Value;

    public static byte[] Encode(XstFile file, Message item)
    {
        using var mime = ConvertMessage(file, item, 0);
        using var output = new MemoryStream();
        mime.WriteTo(output);
        if (output.Length > 25 * 1024 * 1024) throw new InvalidDataException("重建的 MIME 超过邮箱的 25 MiB 单封限制。");
        return output.ToArray();
    }

    private static MimeMessage ConvertMessage(XstFile file, Message item, int depth)
    {
        if (depth >= 32) throw new InvalidDataException("嵌套邮件超过可阅读的深度。");
        var mime = new MimeMessage();
        mime.Headers.Clear();
        // A PST stores MAPI properties. Retain the available transport headers and rebuild only the MIME structure.
        if (Property(item, 0x007d) is string headers)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(headers + "\r\n\r\n"));
            foreach (var header in HeaderList.Load(input))
                if (!header.Field.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && header.Id != HeaderId.MimeVersion)
                {
                    if (header.Id is HeaderId.Date or HeaderId.MessageId or HeaderId.Subject)
                        mime.Headers.RemoveAll(header.Id);
                    mime.Headers.Add(header);
                }
        }
        mime.Subject = item.Subject ?? mime.Subject;
        if (item.Submitted is { } sent) mime.Date = sent;
        else if (item.Received is { } received && mime.Headers[HeaderId.Date] == null) mime.Date = received;
        if (Property(item, 0x1035) is string id && id.Trim('<', '>').Contains('@')) mime.MessageId = id.Trim('<', '>');
        if (mime.From.Count == 0) AddAddress(mime.From, item.From, Property(item, 0x5d01) as string ?? Property(item, 0x0c1f) as string);
        foreach (var group in item.Recipients.GroupBy(r => r.RecipientType))
        {
            var addresses = group.Key switch { RecipientType.To => mime.To, RecipientType.Cc => mime.Cc, RecipientType.Bcc => mime.Bcc, _ => null };
            if (addresses == null || addresses.Count > 0) continue;
            foreach (var recipient in group)
                AddAddress(addresses, recipient.DisplayName, recipient.Properties.FirstOrDefault(p => (ushort)p.Tag == 0x39fe)?.Value as string ?? recipient.EmailAddress);
        }
        var body = new BodyBuilder { TextBody = item.Body, HtmlBody = item.BodyHtml != null || item.Html != null ? item.GetBodyAsHtmlString() : null };
        if (item.RtfCompressed is { Length: > 0 })
        {
            using var source = new MemoryStream(item.RtfCompressed, false);
            using var decoded = new RtfDecompressor().Decompress(source, true);
            var rtf = decoded.ToArray();
            body.TextBody ??= PstBodyText.FromRtf(Encoding.Latin1.GetString(rtf));
            body.Attachments.Add("正文.rtf", rtf, new ContentType("application", "rtf"));
        }
        foreach (var attachment in item.Attachments)
        {
            if (attachment.IsEmail)
            {
                var embedded = ConvertMessage(file, file.OpenAttachedMessage(attachment), depth + 1);
                var part = new MessagePart { Message = embedded, ContentDisposition = new ContentDisposition("attachment") };
                part.ContentDisposition.FileName = attachment.FileName ?? "嵌入邮件.eml";
                body.Attachments.Add(part);
                continue;
            }
            if (!attachment.IsFile) throw new InvalidDataException("附件类型无法还原：" + attachment.Type);
            using var data = new MemoryStream();
            file.SaveAttachment(data, attachment);
            var name = attachment.FileName ?? attachment.DisplayName ?? "附件";
            var parts = string.IsNullOrEmpty(attachment.ContentId) ? body.Attachments : body.LinkedResources;
            var type = ContentType.TryParse(attachment.MimeTag ?? "", out var parsed) ? parsed : ContentType.Parse(MimeTypes.GetMimeType(name));
            var entity = parts.Add(name, data.ToArray(), type);
            if (!string.IsNullOrEmpty(attachment.ContentId)) entity.ContentId = attachment.ContentId.Trim('<', '>');
        }
        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static void AddAddress(InternetAddressList target, string name, string value)
    {
        if (MailboxAddress.TryParse(value ?? name ?? "", out var address))
        {
            if (!string.IsNullOrWhiteSpace(name)) address.Name = name;
            target.Add(address);
        }
    }
}
