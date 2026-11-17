using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MyMailBox.Contracts;
using System.Net;
using System.Text.RegularExpressions;

namespace MyMailBox.Mail;

public sealed class HtmlMailRenderer : IHtmlMailRenderer
{
    private static readonly HashSet<string> Tags = new("html body div span p br hr h1 h2 h3 h4 h5 h6 b strong i em u s blockquote pre code ul ol li table thead tbody tfoot tr td th caption colgroup col a img audio video source".Split(' '));
    private static readonly HashSet<string> Styles = new("color background-color font-family font-size font-weight font-style text-decoration text-align vertical-align border border-color border-width border-style border-collapse padding padding-left padding-right padding-top padding-bottom margin margin-left margin-right margin-top margin-bottom line-height white-space width max-width height".Split(' '));
    public Task<SafeMailDocument> Render(ParsedMail mail, string baseUri, IReadOnlyDictionary<string, MailResource>? loadedRemote = null, CancellationToken cancellation = default) => Task.Run(async () =>
    {
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.Host != "mail.mymailbox.invalid") throw new ArgumentException("HTML 阅读来源无效。");
        var document = await new HtmlParser().ParseDocumentAsync(mail.Html, cancellation);
        var resources = new List<MailResource>(); var remote = new List<RemoteMailResource>(); var warnings = new List<string>();
        foreach (var element in document.All.ToArray())
        {
            cancellation.ThrowIfCancellationRequested();
            var tag = element.LocalName;
            if (tag is "head" or "script" or "style" or "iframe" or "object" or "embed" or "form" or "input" or "button" or "textarea" or "select" or "svg" or "math" or "link" or "meta" or "base") { element.Remove(); continue; }
            if (!Tags.Contains(tag)) { element.Replace(document.CreateTextNode(element.TextContent)); continue; }
            var style = element.GetAttribute("style"); var href = element.GetAttribute("href"); var src = element.GetAttribute("src"); var alt = element.GetAttribute("alt");
            if (tag is "audio" or "video") src ??= element.QuerySelector("source")?.GetAttribute("src");
            var colspan = element.GetAttribute("colspan"); var rowspan = element.GetAttribute("rowspan");
            foreach (var attribute in element.Attributes.ToArray()) element.RemoveAttribute(attribute.Name);
            if (style is not null)
            {
                var safe = string.Join(';', style.Split(';').Select(declaration => declaration.Split(':', 2)).Where(p => p.Length == 2 && Styles.Contains(p[0].Trim().ToLowerInvariant()) && Regex.IsMatch(p[1], @"^[a-zA-Z0-9#%.,()\s-]+$") && !Regex.IsMatch(p[1], "url|expression|import|javascript", RegexOptions.IgnoreCase)).Select(p => p[0].Trim().ToLowerInvariant() + ":" + p[1].Trim()));
                if (safe.Length > 0) element.SetAttribute("style", safe);
            }
            if (tag is "td" or "th")
            {
                if (int.TryParse(colspan, out var columns) && columns is > 0 and <= 50) element.SetAttribute("colspan", columns.ToString());
                if (int.TryParse(rowspan, out var rows) && rows is > 0 and <= 100) element.SetAttribute("rowspan", rows.ToString());
            }
            if (tag == "a" && Uri.TryCreate(href, UriKind.Absolute, out var link) && link.Scheme is "http" or "https") { element.SetAttribute("href", link.AbsoluteUri); element.SetAttribute("rel", "noreferrer noopener"); }
            if (tag is "img" or "audio" or "video")
            {
                MailResource? resource = null; string? key = null;
                if (src?.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var cid = Uri.UnescapeDataString(src[4..]).Trim('<', '>'); resource = mail.InlineResources?.FirstOrDefault(r => r.Key == cid); key = "cid-" + resources.Count;
                }
                else if (Uri.TryCreate(src, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                {
                    key = "remote-" + remote.Count; remote.Add(new(key, url.AbsoluteUri, tag)); loadedRemote?.TryGetValue(key, out resource);
                }
                if (resource is not null && AllowedType(tag, resource.ContentType))
                {
                    resource = resource with { Key = key! }; resources.Add(resource); element.SetAttribute("src", baseUri + "resources/" + key);
                    if (tag != "img") { element.SetAttribute("controls", ""); element.SetAttribute("preload", "none"); }
                    else element.SetAttribute("alt", alt ?? "内联图片");
                }
                else
                {
                    var placeholder = document.CreateElement("span"); placeholder.TextContent = src?.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) == true ? "[内联图片不可用]" : "[远程多媒体已阻止]";
                    element.Replace(placeholder);
                }
            }
            else if (tag == "source") element.Remove();
        }
        if (remote.Count > 0) warnings.Add($"已识别 {remote.Count} 项远程多媒体；默认不发起网络请求。");
        var csp = "default-src 'none'; script-src 'none'; connect-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; font-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
        var body = document.Body?.InnerHtml ?? "<p>无 HTML 正文，可切换纯文本或原文。</p>";
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"" + WebUtility.HtmlEncode(csp) + "\"><style>body{font:14px 'Segoe UI',sans-serif;line-height:1.6;color:#172033;background:#fff;margin:16px;overflow-wrap:anywhere}img,video{max-width:100%;height:auto}table{max-width:100%;border-collapse:collapse}td,th{padding:4px}pre{white-space:pre-wrap}a{color:#1d4ed8}</style></head><body>" + body + "</body></html>";
        return new SafeMailDocument(html, resources, remote, warnings.ToArray());
    }, cancellation);
    public static bool AllowedType(string kind, string type) => kind switch
    {
        "img" => type is "image/png" or "image/jpeg" or "image/gif" or "image/webp",
        "audio" => type is "audio/mpeg" or "audio/ogg" or "audio/wav",
        "video" => type is "video/mp4" or "video/webm",
        _ => false
    };
}
