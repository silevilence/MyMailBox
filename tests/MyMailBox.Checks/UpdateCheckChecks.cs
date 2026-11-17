using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;
using MyMailBox.Updates;

internal static class UpdateCheckChecks
{
    public static async Task Run()
    {
        var clock = new Clock(); var calls = 0;
        var json = JsonSerializer.Serialize(new[] { Release("V1.2.0"), Release("v1.10.0"), Release("V2.0.0-beta.1", preview: true), Release("V9.0.0", draft: true), Release("garbage") });
        using var http = new HttpClient(new Handler((r, _) =>
        {
            calls++; Check(r.Headers.Authorization is null && r.Headers.UserAgent.Count > 0, "anonymous request headers");
            if (calls > 1) { Check(r.Headers.IfNoneMatch.Any(x => x.Tag == "\"etag\""), "conditional cache header"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
            var response = Response(json); response.Headers.ETag = new EntityTagHeaderValue("\"etag\""); return Task.FromResult(response);
        }));
        var source = new GitHubReleaseSource("owner/repository", http, clock);
        var stable = await source.Check("1.0.0", UpdateChannel.Stable);
        Check(stable.State == UpdateCheckState.Available && stable.Release?.Version == "1.10.0", "stable SemVer, draft and prerelease filter");
        Check((await source.Check("1.0.0", UpdateChannel.Preview)).Release?.Version == "2.0.0-beta.1", "preview and 304 cache");
        Check((await source.Check("1.10.0+local", UpdateChannel.Stable)).State == UpdateCheckState.Current, "build metadata comparison");
        using var missingHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new[] { Release("1.1.0", missing: true) })))));
        Check((await new GitHubReleaseSource("o/r", missingHttp).Check("1.0.0", UpdateChannel.Stable)).State == UpdateCheckState.MissingAssets, "missing win-x64 assets distinct");
        using var badHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Response("{bad"))));
        Check((await new GitHubReleaseSource("o/r", badHttp).Check("1.0.0", UpdateChannel.Stable)).State == UpdateCheckState.Unavailable, "bad json distinct from current");
        using var proxyHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired))));
        Check((await new GitHubReleaseSource("o/r", proxyHttp).Check("1.0.0", UpdateChannel.Stable)).Message.Contains("407"), "proxy diagnostic");
        using var slowHttp = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("timeout")));
        Check((await new GitHubReleaseSource("o/r", slowHttp).Check("1.0.0", UpdateChannel.Stable)).Message.Contains("超时"), "timeout distinct");
        using var rateHttp = new HttpClient(new Handler((_, _) => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)); return Task.FromResult(r); }));
        var rate = await new GitHubReleaseSource("o/r", rateHttp, clock).Check("1.0.0", UpdateChannel.Stable);
        Check(rate.State == UpdateCheckState.RateLimited && rate.RetryAfterUtc == clock.GetUtcNow().AddMinutes(2), "rate limit and retry time");
        var root = Path.Combine(PluginChecks.Repository, "artifacts/checks/update-settings-" + Guid.NewGuid().ToString("N"));
        var alternate = new TestSource("local-test", stable, clock);
        using (var service = new UpdateService(root, "1.0.0", "o/r", _ => alternate, clock))
        {
            Check((await service.Check(false)).State == UpdateCheckState.NotChecked && alternate.Count == 0, "background opt-in");
            await service.Save(new("o/r", Background: true));
            await service.Check(false); await service.Check(true); Check(alternate.Count == 1, "manual request minimum cache");
            await service.Skip("V1.10.0"); clock.Advance(TimeSpan.FromHours(6));
            Check((await service.Check(false)).State == UpdateCheckState.Skipped, "skip persisted and background suppressed");
            clock.Advance(TimeSpan.FromSeconds(31)); Check((await service.Check(true)).State == UpdateCheckState.Available, "manual reveals skipped version");
            alternate.Result = rate with { CheckedUtc = clock.GetUtcNow(), RetryAfterUtc = clock.GetUtcNow().AddMinutes(2) };
            clock.Advance(TimeSpan.FromSeconds(31)); await service.Check(true); var before = alternate.Count;
            clock.Advance(TimeSpan.FromSeconds(31)); Check((await service.Check(true)).Cached && alternate.Count == before, "rate retry respected");
        }
        using (var restarted = new UpdateService(root, "1.0.0", "o/r", _ => alternate, clock))
        { Check((await restarted.Snapshot()).Settings.SkippedVersion == "1.10.0" && (await restarted.Check(true)).Cached, "restart settings and cache retained"); }
        using (var upgraded = new UpdateService(root, "1.10.0", "o/r", _ => alternate, clock))
        { Check((await upgraded.Snapshot()).Last.State == UpdateCheckState.NotChecked, "upgraded version invalidates old cache"); }
        var pages = new Contributions(); await using var runtime = new PluginRuntime(pages);
        var plugin = new PluginDefinition(new("updates-test", "1.0.0", 1, 1, PluginKind.Ordinary, "updates-test"), PluginOrigin.Builtin, "", (ctx, _) => UpdatePlugin.Apply(ctx, Path.Combine(root, "plugin"), null, alternate));
        await runtime.Start(plugin, new() { InstanceId = "updates-test" });
        var contract = pages.Pages.Single().Updates!; Check((await contract.Check(true)).State == UpdateCheckState.RateLimited, "second source supplied through plugin");
        await runtime.Stop("updates-test"); Check(pages.Pages.Count == 0, "update page removed on unload");
        await PluginChecks.Reject(() => contract.Check(true), "停止");
        Console.WriteLine("PASS update checks: GitHub SemVer/channels/assets, 304, rate limits, proxy/timeout, persistent settings/cache/skip, alternative source and plugin drain");
    }
    public static async Task Live()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-source.json")));
        var repository = config.RootElement.GetProperty("repository").GetString()!;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var result = await new GitHubReleaseSource(repository, http).Check("0.1.0", UpdateChannel.Stable);
        Check(result.State is UpdateCheckState.Current or UpdateCheckState.Available or UpdateCheckState.MissingAssets, "live public GitHub result: " + result.Message);
        Console.WriteLine($"PASS live public GitHub read: {repository}: {result.State} — {result.Message}; no token, no asset downloaded");
    }
    private static object Release(string version, bool preview = false, bool draft = false, bool missing = false) => new
    {
        tag_name = version, prerelease = preview, draft, body = "更新说明", html_url = "https://github.com/o/r/releases/tag/" + version,
        assets = (missing ? new[] { "wrong-win-arm64.exe" } : new[] { $"MyMailBox-V{version.TrimStart('v', 'V')}-win-x64-setup.exe", "appcast.xml", "appcast.xml.signature" })
            .Select(name => new { name, size = 123, state = "uploaded", browser_download_url = "https://github.com/o/r/releases/download/" + version + "/" + name }).ToArray()
    };
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string message) => PluginChecks.Check(value, "updates: " + message);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
    private sealed class Clock : TimeProvider
    { private DateTimeOffset _now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => _now; public void Advance(TimeSpan duration) => _now += duration; }
    private sealed class TestSource(string id, UpdateCheck initial, TimeProvider clock) : IUpdateSource
    { public int Count; public UpdateCheck Result = initial; public string Id => id; public Task<UpdateCheck> Check(string version, UpdateChannel channel, CancellationToken cancellation = default) { Count++; return Task.FromResult(Result with { CheckedUtc = clock.GetUtcNow() }); } }
}
