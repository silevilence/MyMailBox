using System.Net;
using System.Net.Http.Headers;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class RemoteContentChecks
{
    public static async Task Run()
    {
        var store = new SqliteMailStore(Path.Combine(Path.GetTempPath(), "MyMailBox-remote-" + Guid.NewGuid().ToString("N")));
        var handler = new ResourceHandler(); using var service = new RemoteContentService(store, handler);
        var context = new RemoteContentContext(Guid.NewGuid(), "Trusted name <sender@example.test>");
        RemoteMailResource[] resources = [new("image", "https://assets.example.test/image", "img"), new("audio", "https://assets.example.test/audio", "audio"), new("video", "https://assets.example.test/video", "video")];
        MailChecks.Check((await service.Load(context, resources)).Resources.Count == 0 && handler.Requests == 0, "Default initiated network");
        await service.SetRule(context, RemoteContentScope.Message, true);
        var first = await service.Load(context, resources); MailChecks.Check(first.Resources.Count == 3 && handler.Requests == 3, "Explicit media load failed");
        await service.Load(context, resources); MailChecks.Check(handler.Requests == 3, "Repeat download ignored local cache");
        MailChecks.Check((await service.Preview(context with { MessageId = Guid.NewGuid() })).MatchingRules.Length == 0, "Per-message grant escaped scope");
        await service.SetRule(context, RemoteContentScope.Message, false);
        MailChecks.Check((await service.Load(context, resources)).Resources.Count == 0, "Revoked message still displays cached content");
        await service.SetRule(context, RemoteContentScope.Sender, true);
        MailChecks.Check((await service.Preview(context with { MessageId = Guid.NewGuid(), From = "Different name <sender@example.test>" })).MatchingRules.Length == 1, "Sender depends on display name");
        MailChecks.Check((await service.Preview(context with { From = "Trusted name <attacker@evil.test>" })).MatchingRules.Length == 0, "Spoofed display name trusted");
        await service.SetRule(context, RemoteContentScope.Domain, true);
        MailChecks.Check((await service.Preview(context with { From = "other@example.test" })).MatchingRules.Any(r => r.Scope == RemoteContentScope.Domain), "Domain did not match");
        MailChecks.Check((await service.Preview(context with { From = "other@sub.example.test" })).MatchingRules.Length == 0, "Domain rule includes subdomains");
        var failures = await service.Load(context, [new("redirect", "https://assets.example.test/redirect", "img"), new("large", "https://assets.example.test/large", "img"), new("script", "https://assets.example.test/script", "img"), new("offline", "https://assets.example.test/offline", "img"), new("local", "http://127.0.0.1/private", "img")]);
        MailChecks.Check(failures.Resources.Count == 0 && failures.Results.Any(r => r.Contains("重定向")) && failures.Results.Any(r => r.Contains("10 MiB")) && failures.Results.Any(r => r.Contains("不受支持")), "Failed resources not explained");
        MailChecks.Check(!handler.PrivateRequested && !handler.CredentialsSent, "Local target or credentials leaked");
        using var reopened = new RemoteContentService(store, new ResourceHandler());
        MailChecks.Check((await reopened.Preview(context)).MatchingRules.Length == 2, "Rules not durable");
        await reopened.SetRule(context, RemoteContentScope.Sender, false); await reopened.SetRule(context, RemoteContentScope.Domain, false);
        MailChecks.Check((await reopened.Preview(context)).MatchingRules.Length == 0, "Revoke incomplete");
        foreach (var address in new[] { "127.0.0.1", "10.1.1.1", "172.16.1.1", "192.168.0.1", "169.254.1.1", "::1", "fc00::1", "fe80::1" }) MailChecks.Check(!RemoteContentService.IsPublicAddress(IPAddress.Parse(address)), "Private address accepted: " + address);
        MailChecks.Check(RemoteContentService.IsPublicAddress(IPAddress.Parse("1.1.1.1")), "Public address rejected");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await service.Load(context, resources, cancellation.Token); throw new Exception("Remote cancel ignored"); } catch (OperationCanceledException) { }
        Console.WriteLine("PASS remote content: zero default requests, message/sender/exact-domain grants, spoofed display name, cache reuse, revoke/reopen, image/audio/video, redirects/size/type/offline/private targets, cancellation");
    }
    private sealed class ResourceHandler : HttpMessageHandler
    {
        public int Requests; public bool PrivateRequested; public bool CredentialsSent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; PrivateRequested |= request.RequestUri!.IsLoopback; CredentialsSent |= request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
            if (request.RequestUri.AbsolutePath == "/offline") throw new HttpRequestException("离线测试");
            if (request.RequestUri.AbsolutePath == "/redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1/private") } });
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue(request.RequestUri.AbsolutePath switch { "/audio" => "audio/mpeg", "/video" => "video/mp4", "/script" => "text/html", _ => "image/png" });
            if (request.RequestUri.AbsolutePath == "/large") content.Headers.ContentLength = RemoteContentService.MaxResourceBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
