namespace MyMailBox.Contracts;

public enum UpdateChannel { Stable, Preview }
public enum UpdateCheckState { NotChecked, Available, Current, Skipped, MissingAssets, RateLimited, Unavailable }
public sealed record UpdateAsset(string Name, Uri Url, long Size, string? Sha256 = null);
public sealed record UpdateRelease(string Version, string Notes, Uri Page, UpdateAsset Installer, UpdateAsset AppCast, UpdateAsset Signature);
public sealed record UpdateCheck(UpdateCheckState State, string Message, DateTimeOffset CheckedUtc, UpdateRelease? Release = null)
{
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public bool Cached { get; init; }
}
public sealed record UpdateSettings(string Repository, UpdateChannel Channel = UpdateChannel.Stable, bool Background = false, int IntervalHours = 6, string? SkippedVersion = null);
public sealed record UpdateSnapshot(string CurrentVersion, UpdateSettings Settings, UpdateCheck Last);
public interface IUpdateSource
{
    string Id { get; }
    Task<UpdateCheck> Check(string currentVersion, UpdateChannel channel, CancellationToken cancellation = default);
}
public interface IUpdateService
{
    Task<UpdateSnapshot> Snapshot(CancellationToken cancellation = default);
    Task Save(UpdateSettings settings, CancellationToken cancellation = default);
    Task<UpdateCheck> Check(bool manual, CancellationToken cancellation = default);
    Task Skip(string? version, CancellationToken cancellation = default);
}
public enum UpdateInstallState { NotConfigured, Idle, Downloading, Ready, Queued, Failed }
public sealed record UpdateInstallStatus(UpdateInstallState State, string Message, string? Version = null, int Percent = 0);
public interface IUpdateInstaller
{
    Task<UpdateInstallStatus> Status(CancellationToken cancellation = default);
    Task<UpdateInstallStatus> Download(UpdateRelease release, IProgress<UpdateInstallStatus>? progress = null, CancellationToken cancellation = default);
    Task RequestInstall(CancellationToken cancellation = default);
}
public sealed record UpdatePreparation(string Id, Func<CancellationToken, Task> Save);
