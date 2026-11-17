using MimeKit;
using MyMailBox.Contracts;
using System.Text;

namespace MyMailBox.Mail;

public sealed class MimeReader : IMimeReader
{
    public const int MaxMessageBytes = 25 * 1024 * 1024;
    public const int MaxTextCharacters = 1024 * 1024;
    public const int MaxParts = 2048;

    public Task<ParsedMail> Parse(byte[] raw, CancellationToken cancellation = default) => Task.Run(() =>
    {
        if (raw.Length is 0 or > MaxMessageBytes) throw new InvalidDataException("邮件为空或超过 25 MiB 阅读限制。");
        cancellation.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(raw, false);
        var options = ParserOptions.Default.Clone();
        options.MaxMimeDepth = 32;
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
            var warnings = new List<string>(); var parts = new List<MimePartInfo>(); var text = new StringBuilder(); var html = new StringBuilder();
            if (!MimeKit.Utils.DateUtils.TryParse(message.Headers[HeaderId.Date] ?? "", out _)) warnings.Add("日期头缺失或无效，显示的日期不作为原始收件时间。");
            if (message.From.Count == 0) warnings.Add("发件人头缺失或无法解析，可查看原文。");
            Visit(message.Body, "1", 0);
            cancellation.ThrowIfCancellationRequested();
            return new ParsedMail(message.Subject ?? "（无主题）", message.From.Count == 0 ? "（发件人未知）" : message.From.ToString(), message.To.ToString(),
                message.Date, text.Length > 0 ? text.ToString() : "此邮件没有可读取的纯文本正文，请切换原文。", message.Cc.ToString(), html.ToString(), parts, warnings.Distinct().ToArray());

            void Visit(MimeEntity entity, string path, int depth)
            {
                cancellation.ThrowIfCancellationRequested();
                if (parts.Count >= MaxParts) { warnings.Add("邮件部件超过 2048 个，仅显示前 2048 个；原文完整保留。"); return; }
                parts.Add(new(path, entity.ContentType.MimeType, entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? "", entity.ContentId, entity.IsAttachment, depth));
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
                        try { if (body.IsPlain) Append(text, body.Text); else if (body.IsHtml) Append(html, body.Text); }
                        catch (Exception error) when (error is FormatException or DecoderFallbackException or NotSupportedException)
                        { warnings.Add($"部件 {path} 正文解码失败，请查看原文。"); }
                        break;
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
