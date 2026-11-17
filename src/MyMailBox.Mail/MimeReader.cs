using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class MimeReader : IMimeReader
{
    public const int MaxMessageBytes = 25 * 1024 * 1024;

    public Task<ParsedMail> Parse(byte[] raw, CancellationToken cancellation = default) => Task.Run(() =>
    {
        if (raw.Length is 0 or > MaxMessageBytes) throw new InvalidDataException("邮件为空或超过 25 MiB 阅读限制。");
        cancellation.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(raw, false);
        var options = ParserOptions.Default.Clone();
        options.MaxMimeDepth = 32;
        using var message = MimeMessage.Load(options, stream, cancellation);
        if (message.Headers.Count == 0 || message.Body is null) throw new InvalidDataException("未找到有效邮件头或正文；请检查 EML 文件。");
        return new ParsedMail(message.Subject ?? "（无主题）", message.From.ToString(), message.To.ToString(),
            message.Date, message.TextBody ?? "此邮件没有纯文本正文。HTML 阅读支持尚未启用。");
    }, cancellation);
}
