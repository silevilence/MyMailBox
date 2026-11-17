using AngleSharp.Html.Parser;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class HtmlChecks
{
    public static async Task Run()
    {
        var html = """
            <html><head><base href="https://evil.test/"><meta http-equiv="refresh" content="0;url=https://evil.test"><style>@import url(https://evil.test/css);@font-face{src:url(https://evil.test/font)}</style></head>
            <body onload="fetch('https://evil.test/script')"><script>chrome.webview.postMessage('secret');alert(1);</script>
            <iframe src="https://evil.test/frame"></iframe><object data="file:///c:/secret"></object><form action="https://evil.test/post"><input name="x"></form>
            <p style="color:red;background-image:url(https://evil.test/background);position:fixed" onclick="alert(1)">Safe marker</p>
            <table><tr><td colspan="2">Table cell</td></tr></table><img src="cid:logo" onerror="alert(1)">
            <img src="https://evil.test/image" srcset="https://evil.test/other 2x"><video><source src="https://evil.test/video"></video><audio src="https://evil.test/audio"></audio>
            <a href="javascript:alert(1)" download="file">bad</a><a href="file:///c:/secret">local</a><a href="https://example.test/page?q=x" target="_blank">external</a>
            <svg onload="alert(1)"><image href="https://evil.test/svg"/></svg></body></html>
            """;
        var mail = new ParsedMail("HTML", "sender@example.test", "", DateTimeOffset.UtcNow, "fallback", Html: html, InlineResources: [new("logo", "image/png", [1, 2, 3])]);
        var renderer = new HtmlMailRenderer(); var result = await renderer.Render(mail, "https://mail.mymailbox.invalid/test/");
        var document = await new HtmlParser().ParseDocumentAsync(result.Html);
        MailChecks.Check(document.QuerySelectorAll("script,iframe,object,form,input,svg,base,link").Length == 0, "Active elements survived");
        MailChecks.Check(!document.All.Any(e => e.Attributes.Any(a => a.Name.StartsWith("on") || a.Name is "srcset" or "download")), "Active attributes survived");
        MailChecks.Check(document.QuerySelector("p")!.GetAttribute("style") == "color:red", "Unsafe CSS survived or safe style lost");
        MailChecks.Check(document.QuerySelector("td")!.TextContent == "Table cell", "Layout content lost");
        MailChecks.Check(document.QuerySelectorAll("img").Length == 1 && document.QuerySelector("img")!.GetAttribute("src")!.StartsWith("https://mail.mymailbox.invalid/test/resources/"), "CID not isolated");
        MailChecks.Check(result.RemoteResources.Count == 3 && result.Resources.Count == 1, "Remote media not classified/blocked");
        MailChecks.Check(document.QuerySelectorAll("a[href]").Length == 1 && document.QuerySelector("a[href]")!.GetAttribute("href")!.StartsWith("https://example.test"), "Unsafe navigation survived");
        var policy = document.QuerySelector("meta[http-equiv='Content-Security-Policy']")!.GetAttribute("content")!;
        MailChecks.Check(policy.Contains("script-src 'none'") && policy.Contains("connect-src 'none'") && policy.Contains("form-action 'none'") && policy.Contains("font-src 'none'"), "CSP incomplete");
        var parsed = await new MimeReader().Parse(MimeChecks.Multipart());
        MailChecks.Check(parsed.InlineResources!.Single().Key == "logo", "MIME CID bytes unavailable");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await renderer.Render(mail, "https://mail.mymailbox.invalid/test/", cancellation: canceled.Token); throw new Exception("HTML cancel ignored"); } catch (OperationCanceledException) { }
        Console.WriteLine("PASS HTML sanitization: scripts/bridges/forms/frames/fonts/CSS/navigation removal, CSP, isolated CID, all remote media blocked, preserved table/text, cancel");
    }
}
