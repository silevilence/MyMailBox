using NetSparkleUpdater;
using NetSparkleUpdater.AppCastHandlers;
using NetSparkleUpdater.Configurations;
using NetSparkleUpdater.Downloaders;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.SignatureVerifiers;
using NuGet.Versioning;

namespace MyMailBox.Updates;

/// <summary>Uses the stable updater without its UI or background loop; the host owns those lifetimes.</summary>
public static class SparkleFactory
{
    public static SparkleUpdater Create(Uri appCast, string publicKey, string currentVersion, string downloadDirectory, bool includePrerelease = false)
    {
        var version = ReleaseVersion.Parse(currentVersion) ?? throw new ArgumentException("当前应用版本无效。");
        var checker = new Ed25519Checker(SecurityMode.Strict, publicKey, publicKeyFile: null, readFileBeingVerifiedInChunks: true);
        if (!checker.HasValidKeyInformation()) throw new ArgumentException("更新签名公钥尚未配置或无效。");
        if (appCast.Scheme != Uri.UriSchemeHttps && !appCast.IsFile && !(appCast.IsLoopback && appCast.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("更新源必须使用 HTTPS；本地演示可使用文件或回环地址。");
        var updater = new SparkleUpdater(appCast.AbsoluteUri, checker)
        {
            Configuration = new MemoryConfiguration(version.ToFullString()),
            UIFactory = null, LogWriter = null, UseNotificationToast = false,
            UserInteractionMode = UserInteractionMode.DownloadNoInstall,
            ShouldKillParentProcessWhenStartingInstaller = true,
            RelaunchAfterUpdate = false,
            TmpDownloadFilePath = Path.GetFullPath(downloadDirectory),
            CheckServerFileName = false
        };
        updater.AppCastHelper.AppCastFilter = new VersionFilter(version, includePrerelease);
        if (appCast.IsFile)
        {
            updater.AppCastDataDownloader = new LocalFileAppCastDownloader { UseLocalUriPath = true };
            updater.UpdateDownloader = new LocalFileDownloader { UseLocalUriPath = true };
        }
        return updater;
    }

    private sealed class VersionFilter(SemanticVersion installed, bool prerelease) : IAppCastFilter
    {
        public IEnumerable<AppCastItem> GetFilteredAppCastItems(SemVerLike _, IEnumerable<AppCastItem> items) => items
            .Select(item => (Item: item, Version: ReleaseVersion.Parse(item.Version)))
            .Where(x => x.Version is not null && (prerelease || !x.Version.IsPrerelease) && VersionComparer.VersionRelease.Compare(x.Version, installed) > 0)
            .OrderByDescending(x => x.Version!, VersionComparer.VersionRelease).Select(x => x.Item);
    }

    private sealed class MemoryConfiguration(string version) : Configuration(new AssemblyInfo(version))
    {
        public override void Reload() { }
    }
    private sealed class AssemblyInfo(string version) : IAssemblyAccessor
    {
        public string AssemblyCompany => "MyMailBox";
        public string AssemblyCopyright => "";
        public string AssemblyDescription => "MyMailBox update";
        public string AssemblyTitle => "MyMailBox";
        public string AssemblyProduct => "MyMailBox";
        public string AssemblyVersion => version;
    }
}
