using MimeKit;
using MyMailBox.Contracts;
using System.Text;

namespace MyMailBox.Mail;

public sealed class MimeReader(IEnumerable<IMimeContentDecoder>? decoders = null) : IMimeReader
{
    private readonly IMimeContentDecoder[] _decoders = decoders?.ToArray() ?? [];
    static MimeReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public const int MaxMessageBytes = 25 * 1024 * 1024;
    public const int MaxTextCharacters = 1024 * 1024;
    public const int MaxParts = 2048;

    public Task<ParsedMail> Parse(byte[] raw, CancellationToken cancellation = default, string? charsetOverride = null) => Task.Run(() =>
    {
        if (raw.Length is 0 or > MaxMessageBytes) throw new InvalidDataException("邮件为空或超过 25 MiB 阅读限制。");
        cancellation.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(raw, false);
        var options = ParserOptions.Default.Clone();
        options.MaxMimeDepth = 32;
        var overrideEncoding = charsetOverride is null ? null : Encoding.GetEncoding(charsetOverride);
        if (overrideEncoding is not null) options.CharsetEncoding = overrideEncoding;
        MimeMessage message;
        try { message = MimeMessage.Load(options, stream, cancellation); }
        catch (FormatException)
        {
            var header = Encoding.UTF8.GetString(raw.AsSpan(0, Math.Min(raw.Length, 8192)));
            if (!header.StartsWith("From:", StringComparison.OrdinalIgnoreCase) && !header.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase) && !header.StartsWith("MIME-Version:", StringComparison.OrdinalIgnoreCase)) throw;
            return new ParsedMail("（邮件解析失败）", "（无法解析）", "", DateTimeOffset.MinValue,
                "邮件正文无法解析，请切换原文查看。原始字节完整保留。", Warnings: ["MIME 格式损坏，未自动修复或丢弃原文。"]);
        }
        using (message)
        {
            if (message.Headers.Count == 0 || message.Body is null) throw new InvalidDataException("未找到有效邮件头或正文；请检查 EML 文件。");
            var warnings = new List<string>(); var parts = new List<MimePartInfo>(); var resources = new List<MailResource>(); var text = new StringBuilder(); var html = new StringBuilder();
            if (!MimeKit.Utils.DateUtils.TryParse(message.Headers[HeaderId.Date] ?? "", out _)) warnings.Add("日期头缺失或无效，显示的日期不作为原始收件时间。");
            if (message.From.Count == 0) warnings.Add("发件人头缺失或无法解析，可查看原文。");
            Visit(message.Body, "1", 0);
            cancellation.ThrowIfCancellationRequested();
            if (text.Length == 0) warnings.Add(html.Length > 0 ? "此邮件没有纯文本正文，请切换安全 HTML 阅读。" : "未找到可读取的正文，可查看原文或附件。");
            return new ParsedMail(message.Subject ?? "（无主题）", message.From.Count == 0 ? "（发件人未知）" : message.From.ToString(), message.To.ToString(),
                message.Date, text.ToString(), message.Cc.ToString(), html.ToString(), parts, warnings.Distinct().ToArray(), resources);

            void Visit(MimeEntity entity, string path, int depth)
            {
                cancellation.ThrowIfCancellationRequested();
                if (parts.Count >= MaxParts) { warnings.Add("邮件部件超过 2048 个，仅显示前 2048 个；原文完整保留。"); return; }
                var type = entity.ContentType.MimeType;
                var fallbackName = type is "application/ms-tnef" or "application/vnd.ms-tnef" ? "winmail.dat" : type is "text/rtf" or "application/rtf" ? "正文.rtf" : "";
                parts.Add(new(path, type, entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? fallbackName, entity.ContentId, entity.IsAttachment, depth));
                if (depth >= 31) { warnings.Add("嵌套内容达到 32 层阅读限制，超出部分请查阅原文。"); return; }
                switch (entity)
                {
                    case Multipart multipart:
                        for (var i = 0; i < multipart.Count && parts.Count < MaxParts; i++) Visit(multipart[i], path + "." + (i + 1), depth + 1);
                        if (parts.Count >= MaxParts) warnings.Add("部件数量达到阅读限制，原文完整保留。");
                        break;
                    case MessagePart nested when nested.Message?.Body is not null:
                        Append(text, $"\n—— 嵌入邮件：{nested.Message.Subject ?? "无主题"} ——\n");
                        Visit(nested.Message.Body, path + ".1", depth + 1); break;
                    case TextPart body when !body.IsAttachment:
                        try
                        {
                            if (body.ContentType.Charset is { } charset)
                            {
                                try { _ = Encoding.GetEncoding(charset); } catch (ArgumentException) { warnings.Add($"部件 {path} 的 charset 不受支持，已尝试回退，可手动选择编码。"); }
                            }
                            var decoded = overrideEncoding is null ? body.Text : body.GetText(overrideEncoding);
                            if (decoded.Contains('\uFFFD')) warnings.Add($"部件 {path} 存在无法解码的字符，可手动选择编码或查看原文。");
                            if (body.IsPlain) Append(text, decoded); else if (body.IsHtml) Append(html, decoded); else DecodeExtension(body);
                        }
                        catch (Exception error) when (error is FormatException or DecoderFallbackException or NotSupportedException)
                        { warnings.Add($"部件 {path} 正文解码失败，请查看原文。"); }
                        break;
                    case MimePart image when image.ContentId is not null && image.Content is not null && image.ContentType.MimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp":
                        if (resources.Sum(r => r.Data.Length) >= 10 * 1024 * 1024) { warnings.Add("内联图片总量超过阅读限制，未加载其余图片。"); break; }
                        using (var decoded = new MemoryStream())
                        {
                            image.Content.DecodeTo(decoded, cancellation);
                            if (decoded.Length <= 2 * 1024 * 1024) resources.Add(new(image.ContentId, image.ContentType.MimeType, decoded.ToArray()));
                            else warnings.Add("内联图片超过 2 MiB，保留在原文中。");
                        }
                        break;
                    case MimePart other:
                        DecodeExtension(other); break;
                }
                void DecodeExtension(MimePart part)
                {
                    var decoder = _decoders.FirstOrDefault(d => d.Supports(type));
                    if (decoder is not null && part.Content is not null)
                    {
                        using var output = new MemoryStream(); part.Content.DecodeTo(output, cancellation);
                        Append(text, decoder.Decode(type, output.ToArray(), cancellation));
                    }
                    else if (type is "text/rtf" or "application/rtf" or "application/ms-tnef" or "application/vnd.ms-tnef")
                        warnings.Add($"{fallbackName} 尚未转换；内容和可能包含的附件仍保存在原文中，可从附件入口保存，或由扩展处理器读取。");
                }
            }
            void Append(StringBuilder target, string value)
            {
                var remaining = MaxTextCharacters - target.Length;
                if (remaining <= 0) { warnings.Add("正文超过显示限制，原文完整保留。"); return; }
                if (value.Length > remaining) warnings.Add("正文超过显示限制，原文完整保留。");
                target.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
                if (target.Length < MaxTextCharacters) target.AppendLine();
            }
        }
    }, cancellation);
}
