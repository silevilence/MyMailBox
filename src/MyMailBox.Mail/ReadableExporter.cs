using System.Net;
using System.Text;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ReadableExporter(IMailStore store, IMimeReader parser, IHtmlMailRenderer renderer, string format, IMailPdfPrinter? printer = null) : IMessageExporter
{
    public string FormatId => format;
    public string Name => format switch { "pdf" => "PDF 阅读副本", "html" => "HTML 阅读副本（离线资源）", "txt" => "纯文本阅读副本", _ => throw new ArgumentException("未知阅读格式。") };
    public async Task<ExportReport> Export(ExportRequest request, IProgress<ExportItemResult>? progress = null, CancellationToken cancellation = default)
    {
        _ = Name;
        var directory = Path.GetFullPath(request.Directory); Directory.CreateDirectory(directory); var results = new List<ExportItemResult>();
        foreach (var item in request.Items.DistinctBy(i => i.MessageId))
        {
            string? temporary = null;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var mail = await parser.Parse(await store.Read(item.MessageId, cancellation), cancellation);
                var stem = EmlExporter.SafeStem(mail.Subject) + "-" + item.MessageId.ToString("N"); var path = Path.Combine(directory, stem + "." + format); var suffix = 2;
                while (File.Exists(path) || Directory.Exists(path)) path = Path.Combine(directory, stem + "-" + suffix++ + "." + format);
                temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
                var header = $"主题：{mail.Subject}\n发件人：{mail.From}\n收件人：{mail.To}\n抄送：{mail.Cc}\n发送时间：{mail.Date:O}\n\n阅读副本不等同完整邮件备份。附件{(request.Options.SaveAttachments ? "另存于附件目录" : "未另存")}。\n\n";
                if (format == "txt")
                {
                    var text = mail.Text;
                    if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(mail.Html))
                    {
                        using var parsed = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(mail.Html, cancellation);
                        foreach (var active in parsed.QuerySelectorAll("script,style,head")) active.Remove();
                        text = parsed.Body?.TextContent ?? "";
                    }
                    await File.WriteAllTextAsync(temporary, header + text, new UTF8Encoding(true), cancellation);
                }
                else
                {
                    var origin = "https://mail.mymailbox.invalid/" + Guid.NewGuid().ToString("N") + "/";
                    var html = "<pre>" + WebUtility.HtmlEncode(header) + "</pre><hr>" + (string.IsNullOrWhiteSpace(mail.Html) ? "<pre>" + WebUtility.HtmlEncode(mail.Text) + "</pre>" : mail.Html);
                    var document = await renderer.Render(mail with { Html = html }, origin, cancellation: cancellation);
                    document = document with { Html = document.Html.Replace("</style>", "@media print{body{font-family:'Microsoft YaHei UI','Segoe UI',sans-serif;font-size:11pt}img,table{max-width:100%}tr,img{break-inside:avoid}h1,h2,h3{break-after:avoid}p{orphans:3;widows:3}}</style>") };
                    if (format == "pdf")
                    {
                        if (printer is null) throw new InvalidOperationException("PDF 导出需要桌面 WebView2 打印服务，请在桌面应用中使用。");
                        await printer.Print(origin + "index.html", document, temporary, cancellation);
                    }
                    else
                    {
                        using var portable = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(document.Html, cancellation);
                        var policy = portable.QuerySelector("meta[http-equiv='Content-Security-Policy']")!;
                        policy.SetAttribute("content", policy.GetAttribute("content")!.Replace("img-src 'self'", "img-src data:").Replace("media-src 'self'", "media-src data:"));
                        var resources = document.Resources.ToDictionary(r => origin + "resources/" + r.Key);
                        foreach (var element in portable.QuerySelectorAll("[src]"))
                            if (resources.TryGetValue(element.GetAttribute("src")!, out var resource))
                                element.SetAttribute("src", "data:" + resource.ContentType + ";base64," + Convert.ToBase64String(resource.Data));
                        await File.WriteAllTextAsync(temporary, "<!doctype html>\n" + portable.DocumentElement!.OuterHtml, new UTF8Encoding(false), cancellation);
                    }
                }
                cancellation.ThrowIfCancellationRequested(); File.Move(temporary, path); temporary = null;
                Report(new(item.MessageId, "success", path, "阅读副本已导出；完整备份请使用 EML 或归档包。"));
                if (request.Options.SaveAttachments)
                {
                    var attachmentRequest = request with { Items = [item], Directory = Path.Combine(directory, "附件") };
                    var attached = await new AttachmentExporter(store, parser, new AttachmentService(store)).Export(attachmentRequest, cancellation: cancellation);
                    foreach (var result in attached.Items) Report(result);
                }
            }
            catch (OperationCanceledException) { Report(new(item.MessageId, "canceled", null, "已取消，已完成文件保留。")); }
            catch (Exception error) { Report(new(item.MessageId, "failed", null, error.Message)); }
            finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
        }
        return new(results.ToArray());
        void Report(ExportItemResult result) { results.Add(result); progress?.Report(result); }
    }
}
