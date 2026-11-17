using System.Text;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class EncodingChecks
{
    public static async Task Run()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); var parser = new MimeReader();
        foreach (var (charset, text) in new[] { ("utf-8", "中文与日本語"), ("gb18030", "中文邮件测试"), ("gbk", "中文邮件测试"), ("big5", "繁體郵件測試"), ("shift_jis", "日本語メール"), ("windows-1252", "café déjà vu") })
        {
            var raw = Message(charset, Encoding.GetEncoding(charset).GetBytes(text));
            MailChecks.Check((await parser.Parse(raw)).Text.Contains(text), "Charset failed: " + charset);
        }
        var mislabeled = Message("utf-8", Encoding.GetEncoding("gb18030").GetBytes("错误声明的中文正文"));
        var corrected = await parser.Parse(mislabeled, charsetOverride: "gb18030");
        MailChecks.Check(corrected.Text.Contains("错误声明的中文正文"), "Manual override failed");
        var unknown = await parser.Parse(Message("unknown-mail-charset", Encoding.UTF8.GetBytes("回退内容")));
        MailChecks.Check(unknown.Warnings!.Any(w => w.Contains("charset")), "Unknown charset silently ignored");
        foreach (var type in new[] { "application/ms-tnef", "application/rtf", "text/rtf" })
        {
            var raw = Encoding.ASCII.GetBytes($"From: sender@example.test\r\nSubject: Legacy\r\nContent-Type: {type}\r\n\r\n{{\\rtf1 legacy body}}\r\n");
            var fallback = await parser.Parse(raw);
            MailChecks.Check(fallback.Warnings!.Any(w => w.Contains("尚未转换")) && fallback.Parts!.Any(p => p.Name.Length > 0), "Legacy content silently lost: " + type);
            var replacement = await new MimeReader([new Decoder()]).Parse(raw);
            MailChecks.Check(replacement.Text.Contains("扩展处理器"), "Content decoder not replaceable");
        }
        var damaged = await parser.Parse(Encoding.UTF8.GetBytes("From: broken\r\nSubject: Damaged\r\nContent-Type: multipart/mixed; boundary=missing\r\n\r\nContent without boundary"));
        MailChecks.Check(damaged.Parts is not null || damaged.Warnings!.Count > 0, "Damaged MIME has no recovery surface");
        var badHtml = await new HtmlMailRenderer().Render(new("", "", "", DateTimeOffset.UtcNow, "", Html: "<table><tr><td>未闭合<p>仍可读<img src=x onerror=alert(1)>"), "https://mail.mymailbox.invalid/encoding/");
        MailChecks.Check(badHtml.Html.Contains("仍可读") && !badHtml.Html.Contains("onerror"), "Malformed HTML fallback failed");
        Console.WriteLine("PASS encoding: UTF-8/GB18030/GBK/Big5/Japanese/Western fixtures, mislabeled manual override, unknown charset, RTF/TNEF explicit retention, replacement decoder, damaged MIME/HTML");
    }
    private static byte[] Message(string charset, byte[] body) => Encoding.ASCII.GetBytes($"From: sender@example.test\r\nSubject: =?utf-8?B?5Lit5paH5Li76aKY?=\r\nContent-Type: text/plain; charset={charset}\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(body));
    private sealed class Decoder : IMimeContentDecoder
    {
        public bool Supports(string contentType) => true;
        public string Decode(string contentType, byte[] content, CancellationToken cancellation = default) => "扩展处理器：" + content.Length;
    }
}
