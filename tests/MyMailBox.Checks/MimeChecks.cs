using System.Text;
using MyMailBox.Mail;

internal static class MimeChecks
{
    public static byte[] Multipart() => Encoding.UTF8.GetBytes("""
        From: =?utf-8?B?5byg5LiJ?= <sender@example.test>
        To: reader@example.test
        Cc: copied@example.test
        Subject: =?utf-8?B?5Lit5paH5Li76aKY?=
        Date: Tue, 17 Nov 2026 08:30:00 +0800
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary=outer

        --outer
        Content-Type: multipart/alternative; boundary=inner

        --inner
        Content-Type: text/plain; charset=utf-8
        Content-Transfer-Encoding: base64

        5L2g5aW977yM5LiW55WM44CC
        --inner
        Content-Type: text/html; charset=utf-8

        <html><body><p>Hello</p><img src="cid:logo"></body></html>
        --inner--
        --outer
        Content-Type: application/octet-stream; name="data.bin"
        Content-Disposition: attachment; filename="data.bin"
        Content-Transfer-Encoding: base64

        AAEC//4=
        --outer
        Content-Type: image/png
        Content-ID: <logo>
        Content-Transfer-Encoding: base64

        iVBORw0KGgo=
        --outer
        Content-Type: message/rfc822

        From: nested@example.test
        To: reader@example.test
        Subject: Nested mail
        Date: Mon, 16 Nov 2026 12:00:00 +0000
        Content-Type: text/plain; charset=utf-8

        Nested body.
        --outer--
        """.Replace("\n", "\r\n"));

    public static async Task Run()
    {
        var parser = new MimeReader(); var raw = Multipart(); var parsed = await parser.Parse(raw);
        MailChecks.Check(parsed.Subject == "中文主题" && parsed.From.Contains("张三"), "Encoded headers failed");
        MailChecks.Check(parsed.Text.Contains("你好，世界。") && parsed.Text.Contains("Nested body"), "Multipart/nested body failed");
        MailChecks.Check(parsed.Cc.Contains("copied@example.test") && parsed.Date.Offset == TimeSpan.FromHours(8), "Addresses or RFC date lost");
        MailChecks.Check(parsed.Html.Contains("cid:logo") && parsed.Parts!.Any(p => p.ContentId == "logo" && !p.Attachment), "CID structure lost");
        MailChecks.Check(parsed.Parts!.Any(p => p.Attachment && p.Name == "data.bin") && parsed.Parts.Any(p => p.ContentType == "message/rfc822"), "Attachment tree incomplete");
        var badHeaders = await parser.Parse(Encoding.UTF8.GetBytes("Subject: Missing headers\r\nDate: nonsense\r\nContent-Type: text/plain\r\n\r\nFallback body"));
        MailChecks.Check(badHeaders.Warnings!.Count >= 2 && badHeaders.Text.Contains("Fallback body"), "Header fallback missing");
        var deep = new StringBuilder("From: sender@example.test\r\n");
        for (var i = 0; i < 100; i++) deep.Append($"Content-Type: multipart/mixed; boundary=b{i}\r\n\r\n--b{i}\r\n");
        deep.Append("Content-Type: text/plain\r\n\r\ndeep\r\n");
        for (var i = 99; i >= 0; i--) deep.Append($"--b{i}--\r\n");
        var limited = await parser.Parse(Encoding.UTF8.GetBytes(deep.ToString()));
        MailChecks.Check(limited.Parts!.Count <= 33 && limited.Warnings!.Any(w => w.Contains("32")), "Nesting limit missing");
        var largeText = await parser.Parse(Encoding.UTF8.GetBytes("From: sender@example.test\r\nContent-Type: text/plain\r\n\r\n" + new string('x', MimeReader.MaxTextCharacters + 100)));
        MailChecks.Check(largeText.Text.Length <= MimeReader.MaxTextCharacters && largeText.Warnings!.Any(w => w.Contains("限制")), "Display limit failed");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await parser.Parse(raw, canceled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        try { await parser.Parse(new byte[MimeReader.MaxMessageBytes + 1]); throw new Exception("Size limit ignored"); } catch (InvalidDataException) { }
        var store = new SqliteMailStore(Path.Combine(Path.GetTempPath(), "MyMailBox-mime-" + Guid.NewGuid().ToString("N")));
        var id = await store.Import(SqliteMailStore.Inbox, raw, parsed);
        MailChecks.Check((await store.Read(id)).SequenceEqual(raw), "MIME altered by persistence");
        Console.WriteLine("PASS MIME: encoded headers, RFC date, To/Cc, multipart, nested message, attachment/CID tree, fallback, depth/text/size limits, cancellation, raw fidelity");
    }
}
