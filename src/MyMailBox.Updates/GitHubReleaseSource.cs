using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyMailBox.Contracts;
using NuGet.Versioning;

namespace MyMailBox.Updates;

public sealed partial class GitHubReleaseSource : IUpdateSource
{
    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly TimeProvider _clock;
    private readonly Dictionary<int, (string? Etag, string Json)> _cache = [];
    public string Id => "github:" + _repository;
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
    public static string ValidateRepository(string value)
    {
        value = value.Trim();
        if (!RepositoryPattern().IsMatch(value)) throw new ArgumentException("仓库地址请填写 owner/repo。");
        return value;
    }
    public GitHubReleaseSource(string repository, HttpClient http, TimeProvider? clock = null)
    { _repository = ValidateRepository(repository); _http = http; _clock = clock ?? TimeProvider.System; }

    public async Task<UpdateCheck> Check(string currentVersion, UpdateChannel channel, CancellationToken cancellation = default)
    {
        var current = ReleaseVersion.Parse(currentVersion) ?? throw new ArgumentException("当前版本无效。");
        var now = _clock.GetUtcNow();
        try
        {
            JsonElement? selected = null; SemanticVersion? selectedVersion = null;
            for (var page = 1; page <= 10; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases?per_page=100&page={page}");
                request.Headers.UserAgent.ParseAdd("MyMailBox/" + current.ToNormalizedString());
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (_cache.TryGetValue(page, out var prior) && prior.Etag is not null) request.Headers.IfNoneMatch.ParseAdd(prior.Etag);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    var reset = now.AddMinutes(15);
                    if (response.Headers.RetryAfter?.Delta is { } delay) reset = now.Add(delay);
                    else if (response.Headers.RetryAfter?.Date is { } date) reset = date;
                    else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out var seconds)) reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    return new(UpdateCheckState.RateLimited, "GitHub 暂时拒绝请求或达到访问限额，请稍后重试。", now) { RetryAfterUtc = reset > now ? reset : now.AddMinutes(1) };
                }
                string json;
                if (response.StatusCode == HttpStatusCode.NotModified && prior.Json is not null) json = prior.Json;
                else
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("更新响应过大。");
                    await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, cancellation);
                    json = await response.Content.ReadAsStringAsync(cancellation);
                    _cache[page] = (response.Headers.ETag?.ToString(), json);
                }
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("更新响应格式不正确。");
                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    var version = ReleaseVersion.Parse(Text(release, "tag_name"));
                    if (Flag(release, "draft") || version is null || channel == UpdateChannel.Stable && (Flag(release, "prerelease") || version.IsPrerelease)) continue;
                    if (VersionComparer.VersionRelease.Compare(version, current) <= 0 || selectedVersion is not null && VersionComparer.VersionRelease.Compare(version, selectedVersion) <= 0) continue;
                    selected = release.Clone(); selectedVersion = version;
                }
                if (doc.RootElement.GetArrayLength() < 100) break;
                if (page == 10) throw new InvalidDataException("发布记录超过检查上限，未能完成检查。");
            }
            if (selected is not { } latest || selectedVersion is null) return new(UpdateCheckState.Current, "当前通道没有更新版本。", now);
            var versionText = selectedVersion.ToFullString();
            var installer = Asset(latest, $"MyMailBox-V{versionText}-win-x64-setup.exe");
            var appcast = Asset(latest, "appcast.xml"); var signature = Asset(latest, "appcast.xml.signature");
            if (installer is null || appcast is null || signature is null) return new(UpdateCheckState.MissingAssets, $"发现 V{versionText}，但缺少完整的 win-x64 安装包或签名清单，暂不能更新。", now);
            if (!Uri.TryCreate(Text(latest, "html_url"), UriKind.Absolute, out var link) || link.Scheme != "https") throw new InvalidDataException("发布页面地址无效。");
            return new(UpdateCheckState.Available, $"发现新版本 V{versionText}。", now, new(versionText, Text(latest, "body"), link, installer, appcast, signature));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new(UpdateCheckState.Unavailable, "更新检查超时，请检查网络或系统代理。", now); }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException or ArgumentOutOfRangeException)
        { return new(UpdateCheckState.Unavailable, "无法检查更新，请检查仓库地址、网络或系统代理。" + (e is HttpRequestException h && h.StatusCode is { } code ? $" HTTP {(int)code}。" : ""), now); }
    }
    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static UpdateAsset? Asset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(Text(a, "name"), name, StringComparison.OrdinalIgnoreCase) || Text(a, "state") is not ("" or "uploaded")) continue;
            if (!Uri.TryCreate(Text(a, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != "https" || !a.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length <= 0) continue;
            var digest = Text(a, "digest");
            return new(name, url, length, digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : null);
        }
        return null;
    }
}
