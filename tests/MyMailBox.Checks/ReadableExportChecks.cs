using System.Text;
using AngleSharp.Html.Parser;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class ReadableExportChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-readable-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(Path.Combine(directory, "mail")); var parser = new MimeReader(); var renderer = new HtmlMailRenderer();
        var raw = MimeChecks.Multipart(); var mail = await parser.Parse(raw); var id = await store.Import(SqliteMailStore.Inbox, raw, mail);
        var items = new[] { new ExportItem(id, mail.Subject) }; var request = new ExportRequest(items, Path.Combine(directory, "export"));
        var formats = new[] { "pdf", "html", "txt" }; var printer = new RecordingPrinter();
        var paths = new List<string>();
        foreach (var format in formats)
        {
            IMessageExporter exporter = new ReadableExporter(store, parser, renderer, format, printer);
            var result = (await exporter.Export(request)).Items.Single(); MailChecks.Check(result.Status == "success" && File.Exists(result.Path), "Readable format failed: " + format); paths.Add(result.Path!);
            var repeated = (await exporter.Export(request)).Items.Single(); MailChecks.Check(repeated.Path != result.Path, "Conflict overwrote readable result");
            var missing = (await exporter.Export(new([new(Guid.NewGuid(), "missing")], request.Directory))).Items.Single(); MailChecks.Check(missing.Status == "failed", "Missing raw unreported");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            MailChecks.Check((await exporter.Export(request, cancellation: cancel.Token)).Items.Single().Status == "canceled", "Readable cancellation ignored");
        }
        var text = await File.ReadAllTextAsync(paths[2]); MailChecks.Check(text.Contains("中文主题") && text.Contains("copied@example.test") && text.Contains("你好，世界") && text.Contains("附件未另存"), "Text headers/body/attachment notice missing");
        var html = await File.ReadAllTextAsync(paths[1]); using var parsed = await new HtmlParser().ParseDocumentAsync(html);
        var csp = parsed.QuerySelector("meta[http-equiv='Content-Security-Policy']")!.GetAttribute("content")!;
        MailChecks.Check(csp.Contains("script-src 'none'") && csp.Contains("img-src data:") && !html.Contains("https://mail.mymailbox.invalid/"), "Portable HTML CSP/resources incorrect");
        MailChecks.Check(parsed.QuerySelectorAll("img").All(e => e.GetAttribute("src")!.StartsWith("data:image/")), "HTML image not embedded");
        MailChecks.Check(printer.Documents.All(d => d.Html.Contains("@media print") && d.Html.Contains("中文主题") && d.Resources.Count > 0), "PDF print document lacks Chinese/header/resources");
        var hostile = Encoding.UTF8.GetBytes("From: a@example.test\r\nTo: b@example.test\r\nSubject: tracking\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<script>alert(1)</script><img src='https://tracker.example/pixel'><iframe src='https://tracker.example/frame'></iframe><p>中文正文</p>");
        var hostileId = await store.Import(SqliteMailStore.Inbox, hostile, await parser.Parse(hostile));
        var safe = (await new ReadableExporter(store, parser, renderer, "html").Export(new([new(hostileId, "tracking")], request.Directory))).Items.Single();
        var safeHtml = await File.ReadAllTextAsync(safe.Path!); MailChecks.Check(!safeHtml.Contains("tracker.example") && !safeHtml.Contains("<script") && safeHtml.Contains("远程多媒体已阻止"), "Export bypassed reader sanitizer");
        var attached = await new ReadableExporter(store, parser, renderer, "txt").Export(request with { Options = new() { SaveAttachments = true } });
        MailChecks.Check(attached.Items.Any(i => i.Part is not null && i.Status == "success") && File.ReadAllText(attached.Items[0].Path!).Contains("附件另存"), "Explicit attachment option missing");
        MailChecks.Check(!Directory.EnumerateFiles(request.Directory, "*.partial-*", SearchOption.AllDirectories).Any(), "Partial files left behind");
        Console.WriteLine("PASS readable export: three independent exporters, Chinese/header preservation, safe portable HTML/CID/no tracking, attachment choice, conflicts/failure/cancel; PDF renderer contract only, actual pagination pending UI batch");
    }
    private sealed class RecordingPrinter : IMailPdfPrinter
    {
        public List<SafeMailDocument> Documents { get; } = [];
        public Task Print(string uri, SafeMailDocument document, string path, CancellationToken cancellation = default)
        { Documents.Add(document); return File.WriteAllTextAsync(path, "PDF contract test only", cancellation); }
    }
}
