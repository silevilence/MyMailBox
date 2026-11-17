using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyMailBox.Contracts;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.SignatureVerifiers;

namespace MyMailBox.Updates;

public sealed record PreparedUpdate(string Version, string File, long Size, string Signature, string Sha256, string KeyFingerprint)
{
    public int PluginSdk { get; init; } = 1;
}

public sealed class UpdatePackages : IUpdateInstaller, IDisposable
{
    private readonly string _directory, _publicKey, _current, _preparedFile;
    private readonly Action<PreparedUpdate> _requestInstall;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private PreparedUpdate? _prepared;
    private UpdateInstallStatus _status;
    public UpdatePackages(string directory, string publicKey, string currentVersion, Action<PreparedUpdate> requestInstall)
    {
        _directory = Path.GetFullPath(directory); Directory.CreateDirectory(_directory); _preparedFile = Path.Combine(_directory, "prepared.json");
        _publicKey = publicKey; _current = currentVersion; _requestInstall = requestInstall;
        _status = HasKey ? new(UpdateInstallState.Idle, "检查到新版本后可下载并校验。") : new(UpdateInstallState.NotConfigured, "尚未配置发布签名公钥，当前只能检查版本，不能下载安装。");
        if (HasKey && File.Exists(_preparedFile))
        {
            try
            {
                _prepared = JsonSerializer.Deserialize<PreparedUpdate>(File.ReadAllText(_preparedFile));
                if (_prepared is not null) { Validate(_prepared); _status = new(UpdateInstallState.Ready, "已有校验通过的安装包，可继续安装。", _prepared.Version); }
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidDataException or ArgumentException or CryptographicException or FormatException)
            { _prepared = null; _status = new(UpdateInstallState.Failed, "已下载更新不可用，请重新下载校验。"); }
        }
    }
    private bool HasKey => new Ed25519Checker(SecurityMode.Strict, _publicKey, publicKeyFile: null).HasValidKeyInformation();
    private string Fingerprint => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(_publicKey)));
    public Task<UpdateInstallStatus> Status(CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(_status); }
    public async Task<UpdateInstallStatus> Download(UpdateRelease release, IProgress<UpdateInstallStatus>? progress = null, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            if (!HasKey) throw new InvalidOperationException(_status.Message);
            if (new Uri(release.AppCast.Url.AbsoluteUri + ".signature") != release.Signature.Url) throw new InvalidDataException("签名与清单不是同一发布地址。");
            _prepared = null; File.WriteAllText(_preparedFile, "null");
            void Report(UpdateInstallStatus status) { _status = status; progress?.Report(status); }
            Report(new(UpdateInstallState.Downloading, "正在验证签名清单…", release.Version));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var sparkle = SparkleFactory.Create(release.AppCast.Url, _publicKey, _current, _directory, includePrerelease: true);
            var castDownloader = new CastDownloader(_http, timeout.Token);
            sparkle.AppCastDataDownloader = castDownloader;
            var check = await sparkle.CheckForUpdatesQuietly(); timeout.Token.ThrowIfCancellationRequested();
            if (check.Status != UpdateStatus.UpdateAvailable) throw new InvalidDataException("更新清单签名无效、无法读取或不包含更高版本。");
            var item = check.Updates.SingleOrDefault(i => ReleaseVersion.Parse(i.Version)?.ToFullString() == release.Version && i.DownloadLink == release.Installer.Url.AbsoluteUri);
            if (item is null || item.UpdateSize != release.Installer.Size || string.IsNullOrWhiteSpace(item.DownloadSignature)) throw new InvalidDataException("签名清单与选中的版本、安装器或大小不一致。");
            var xml = System.Xml.Linq.XDocument.Parse(castDownloader.Content[release.AppCast.Url.AbsoluteUri]);
            System.Xml.Linq.XNamespace sparkleNs = "http://www.andymatuschak.org/xml-namespaces/sparkle";
            System.Xml.Linq.XNamespace mailboxNs = "urn:mymailbox:update:v1";
            var matchingItem = xml.Descendants("item").SingleOrDefault(x => x.Element("enclosure")?.Attribute(sparkleNs + "version")?.Value == release.Version);
            if (!int.TryParse(matchingItem?.Element(mailboxNs + "sdkVersion")?.Value, out var pluginSdk) || pluginSdk < 1) throw new InvalidDataException("签名清单缺少有效的插件 SDK 兼容信息。");
            sparkle.TmpDownloadFileNameWithExtension = $"MyMailBox-V{release.Version}-win-x64-setup.exe";
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            sparkle.DownloadFinished += (_, path) => done.TrySetResult(path);
            sparkle.DownloadCanceled += (_, _) => done.TrySetCanceled(timeout.Token);
            sparkle.DownloadHadError += (_, _, error) => done.TrySetException(new IOException("下载未完成，请检查网络或代理。", error));
            sparkle.DownloadedFileIsCorrupt += (_, _) => done.TrySetException(new InvalidDataException("安装包签名不正确，已拒绝安装。"));
            sparkle.DownloadedFileThrewWhileCheckingSignature += (_, _) => done.TrySetException(new InvalidDataException("安装包签名无法验证，已拒绝安装。"));
            sparkle.DownloadMadeProgress += (_, _, args) => Report(new(UpdateInstallState.Downloading, "正在下载并校验安装包…", release.Version, args.ProgressPercentage));
            using var stop = timeout.Token.Register(() => { sparkle.CancelFileDownload(); done.TrySetCanceled(timeout.Token); });
            await sparkle.InitAndBeginDownload(item);
            var file = await done.Task;
            timeout.Token.ThrowIfCancellationRequested();
            await using var downloadedStream = File.OpenRead(file);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(downloadedStream, timeout.Token)).ToLowerInvariant();
            if (release.Installer.Sha256 is { Length: > 0 } expected && !string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包 SHA256 与发布资产摘要不一致。");
            var prepared = new PreparedUpdate(release.Version, file, release.Installer.Size, item.DownloadSignature!, digest, Fingerprint) { PluginSdk = pluginSdk };
            Validate(prepared); _prepared = prepared;
            var temp = _preparedFile + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(prepared)); File.Move(temp, _preparedFile, true);
            Report(new(UpdateInstallState.Ready, "签名与文件校验通过。安装前将保存状态、排空任务并备份工作空间。", release.Version, 100));
            return _status;
        }
        catch (Exception e)
        {
            _status = new(UpdateInstallState.Failed, e is OperationCanceledException ? "下载已取消或超时，未执行安装。" : "更新未就绪：" + e.Message);
            throw;
        }
        finally { _gate.Release(); }
    }
    public async Task RequestInstall(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var prepared = _prepared ?? throw new InvalidOperationException("请先下载并校验更新。"); Validate(prepared); cancellation.ThrowIfCancellationRequested();
            // This queues host shutdown; it must return before the plugin scope can be drained.
            _requestInstall(prepared); _status = new(UpdateInstallState.Queued, "正在准备退出和更新；保存或排空失败时不会安装。", prepared.Version);
        }
        catch (Exception e) { _status = new(UpdateInstallState.Failed, "未执行安装：" + e.Message); throw; }
        finally { _gate.Release(); }
    }
    public void Validate(PreparedUpdate prepared)
    {
        if (NuGet.Versioning.VersionComparer.VersionRelease.Compare(ReleaseVersion.Parse(prepared.Version), ReleaseVersion.Parse(_current)) <= 0) throw new InvalidDataException("更新版本不高于当前版本，禁止降级或重复安装。");
        var path = Path.GetFullPath(prepared.File);
        if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件不在下载目录中。");
        if (prepared.KeyFingerprint != Fingerprint) throw new InvalidDataException("更新签名公钥已改变，请重新下载。");
        UpdateVerification.Verify(prepared, _publicKey);
    }
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }

    private sealed class CastDownloader(HttpClient http, CancellationToken cancellation) : IAppCastDataDownloader
    {
        public Dictionary<string, string> Content { get; } = [];
        public string DownloadAndGetAppCastData(string url) => DownloadAndGetAppCastDataAsync(url).GetAwaiter().GetResult();
        public async Task<string> DownloadAndGetAppCastDataAsync(string url)
        {
            var uri = new Uri(url);
            if (uri.IsFile) return Content[url] = await File.ReadAllTextAsync(uri.LocalPath, cancellation);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, cancellation); return Content[url] = await response.Content.ReadAsStringAsync(cancellation);
        }
        public Encoding GetAppCastEncoding() => Encoding.UTF8;
    }
}

public static class UpdateVerification
{
    public static void Verify(PreparedUpdate update, string publicKey)
    {
        if (ReleaseVersion.Parse(update.Version) is null || !File.Exists(update.File) || new FileInfo(update.File).Length != update.Size) throw new InvalidDataException("更新文件版本、大小或路径不正确。");
        using var stream = File.OpenRead(update.File);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件哈希已改变，禁止安装。");
        var checker = new Ed25519Checker(SecurityMode.Strict, publicKey, publicKeyFile: null, readFileBeingVerifiedInChunks: true);
        if (checker.VerifySignatureOfFile(update.Signature, update.File) != ValidationResult.Valid) throw new InvalidDataException("更新文件签名无效，禁止安装。");
    }
}
