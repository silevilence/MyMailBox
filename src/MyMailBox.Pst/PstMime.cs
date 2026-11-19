using System.Text;
using MimeKit;
using XstReader;

namespace MyMailBox.Pst;

internal static class PstMime
{
    public static object Property(Message item, ushort tag) =>
        item.Properties.FirstOrDefault(p => (ushort)p.Tag == tag)?.Value;

    public static byte[] Encode(XstFile file, Message item, List<string> differences)
    {
        using var mime = ConvertMessage(file, item, 0, differences);
        using var output = new MemoryStream();
        mime.WriteTo(output);
        if (output.Length > 25 * 1024 * 1024) throw new InvalidDataException("重建的 MIME 超过邮箱的 25 MiB 单封限制。");
        return output.ToArray();
    }

    private static MimeMessage ConvertMessage(XstFile file, Message item, int depth, List<string> differences)
    {
        if (depth >= 32) throw new InvalidDataException("嵌套邮件超过可阅读的深度。");
        var mime = new MimeMessage();
        mime.Headers.Clear();
        // A PST stores MAPI properties. Retain the available transport headers and rebuild only the MIME structure.
        if (Property(item, 0x007d) is string headers && !string.IsNullOrWhiteSpace(headers))
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
        else differences.Add("传输头缺失，MIME 由 MAPI 属性重建。");
        mime.Subject = item.Subject ?? mime.Subject;
        if (item.Submitted is { } sent) mime.Date = sent;
        else if (item.Received is { } received && mime.Headers[HeaderId.Date] == null) mime.Date = received;
        if (Property(item, 0x1035) is string id && id.Trim('<', '>').Contains('@')) mime.MessageId = id.Trim('<', '>');
        if (mime.From.Count == 0) AddAddress(mime.From, item.From, Property(item, 0x5d01) as string ?? Property(item, 0x0c1f) as string);
        if (mime.From.Count == 0) differences.Add("发件人缺少可解析的邮箱地址。");
        if (item.Submitted == null) differences.Add("源文件无提交时间。");
        if (item.Received == null) differences.Add("源文件无接收时间。");
        foreach (var group in item.Recipients.GroupBy(r => r.RecipientType))
        {
            var addresses = group.Key switch { RecipientType.To => mime.To, RecipientType.Cc => mime.Cc, RecipientType.Bcc => mime.Bcc, _ => null };
            if (addresses == null || addresses.Count > 0) continue;
            foreach (var recipient in group)
            {
                var before = addresses.Count;
                AddAddress(addresses, recipient.DisplayName, recipient.Properties.FirstOrDefault(p => (ushort)p.Tag == 0x39fe)?.Value as string ?? recipient.EmailAddress);
                if (addresses.Count == before) differences.Add("收件人缺少可解析地址：" + recipient.DisplayName);
            }
        }
        var body = new BodyBuilder { TextBody = item.Body, HtmlBody = item.BodyHtml != null || item.Html != null ? item.GetBodyAsHtmlString() : null };
        if (item.Html is { Length: > 0 } && body.HtmlBody == null)
            throw new InvalidDataException("HTML 正文的源编码无法确定。");
        if (item.RtfCompressed is { Length: > 0 })
        {
            using var source = new MemoryStream(item.RtfCompressed, false);
            using var decoded = new RtfDecompressor().Decompress(source, true);
            var rtf = decoded.ToArray();
            body.TextBody ??= PstBodyText.FromRtf(Encoding.Latin1.GetString(rtf));
            body.Attachments.Add("正文.rtf", rtf, new ContentType("application", "rtf"));
            if (item.Body == null) differences.Add("纯文本由 RTF 转换，原 RTF 随邮件保留。");
        }
        foreach (var attachment in item.Attachments)
        {
            if (attachment.IsEmail)
            {
                var nested = file.OpenAttachedMessage(attachment);
                if (nested.IsEncryptedOrSigned) throw new InvalidDataException("嵌入邮件受保护，需要原客户端。");
                var embedded = ConvertMessage(file, nested, depth + 1, differences);
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
            if (name.Equals("winmail.dat", StringComparison.OrdinalIgnoreCase) || type.MimeType is "application/ms-tnef" or "application/vnd.ms-tnef")
                differences.Add("TNEF 以原始附件保留，可导出后查看。");
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
