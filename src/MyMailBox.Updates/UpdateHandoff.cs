using System.Diagnostics;
using System.Text.Json;

namespace MyMailBox.Updates;

public sealed record UpdateJob(PreparedUpdate Package, string InstallDirectory, string Workspace, string Backup, int ParentId, DateTime ParentStartedUtc);
public sealed record UpgradeState(string Version, string State, string Message, string Backup, DateTimeOffset ChangedUtc);

public static class UpdateHandoff
{
    public static void Launch(PreparedUpdate package, string workspace, string backup, string applicationDirectory)
    {
        var source = Path.Combine(applicationDirectory, "update-host");
        if (!File.Exists(Path.Combine(source, "MyMailBox.UpdateHost.exe"))) throw new FileNotFoundException("发布产物缺少更新助手，请使用完整安装包。");
        var work = Path.Combine(workspace, "updates", "helper-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(work, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        File.Copy(Path.Combine(applicationDirectory, "update-source.json"), Path.Combine(work, "update-source.json"));
        using var parent = Process.GetCurrentProcess();
        var job = new UpdateJob(package, Path.GetFullPath(applicationDirectory), Path.GetFullPath(workspace), backup, parent.Id, parent.StartTime.ToUniversalTime());
        var path = Path.Combine(work, "job.json"); File.WriteAllText(path, JsonSerializer.Serialize(job));
        WriteState(job, "waiting-for-exit", "已保存和备份，等待主程序退出后安装。");
        var start = new ProcessStartInfo(Path.Combine(work, "MyMailBox.UpdateHost.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(path); using var helper = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
    }
    public static async Task<int> Execute(UpdateJob job, string publicKey, CancellationToken cancellation = default)
    {
        try
        {
            // The helper runs outside the application directory, so the installer can replace every application DLL.
            Process? parent = null;
            try { parent = Process.GetProcessById(job.ParentId); if (parent.StartTime.ToUniversalTime() != job.ParentStartedUtc) { parent.Dispose(); parent = null; } }
            catch (ArgumentException) { }
            if (parent is not null)
            {
                using (parent) await parent.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(90), cancellation);
            }
            cancellation.ThrowIfCancellationRequested(); UpdateVerification.Verify(job.Package, publicKey);
            if (!Directory.Exists(job.InstallDirectory) || job.InstallDirectory.Contains('"') || job.InstallDirectory.Contains('\r') || job.InstallDirectory.Contains('\n')) throw new InvalidDataException("安装目标目录无效。");
            WriteState(job, "installing", "正在安装已验证的更新。工作空间和备份保留。");
            // NSIS requires /D= to be last and unquoted; direct process execution does not invoke a shell.
            var start = new ProcessStartInfo(job.Package.File, "/S /D=" + Path.TrimEndingDirectorySeparator(job.InstallDirectory))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using var installer = Process.Start(start) ?? throw new IOException("无法启动安装器。");
            await installer.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromMinutes(15), cancellation);
            if (installer.ExitCode != 0) throw new IOException("安装器退出码：" + installer.ExitCode);
            var executable = Path.Combine(job.InstallDirectory, "MyMailBox.Desktop.exe");
            using var check = Process.Start(new ProcessStartInfo(executable, "--runtime-check") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new IOException("无法验证新版本运行时。");
            await check.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            if (check.ExitCode != 0) throw new IOException("新版本运行时验证失败：" + check.ExitCode);
            WriteState(job, "awaiting-startup", "安装完成，等待新版本启动及工作空间检查。");
            var restart = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = job.InstallDirectory };
            restart.Environment["MYMAILBOX_HOME"] = job.Workspace; using var restarted = Process.Start(restart) ?? throw new IOException("无法启动新版本应用。");
            return 0;
        }
        catch (Exception e)
        {
            WriteState(job, "failed", "更新未完成：" + e.Message + "。请保留诊断和备份，勿直接用旧版程序打开已迁移的数据。"); return 1;
        }
    }
    public static UpgradeState? ReadState(string workspace)
    {
        var path = Path.Combine(workspace, "updates", "upgrade-state.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<UpgradeState>(File.ReadAllText(path)); } catch (JsonException) { return null; }
    }
    public static void MarkStartup(string workspace, string currentVersion, bool success, string? error = null)
    {
        var state = ReadState(workspace); if (state?.State != "awaiting-startup") return;
        if (NuGet.Versioning.VersionComparer.VersionRelease.Compare(ReleaseVersion.Parse(state.Version), ReleaseVersion.Parse(currentVersion)) != 0)
        { success = false; error = "启动的应用版本与更新目标不一致。"; }
        WriteState(workspace, state with { State = success ? "completed" : "failed", Message = success ? "新版本已启动，核心配置和本地工作空间检查通过。" : "新版本启动或迁移失败：" + error + "。请从独立备份恢复到新工作空间；不自动回滚数据库。", ChangedUtc = DateTimeOffset.UtcNow });
    }
    public static void WriteState(UpdateJob job, string state, string message) => WriteState(job.Workspace, new(job.Package.Version, state, message, job.Backup, DateTimeOffset.UtcNow));
    private static void WriteState(string workspace, UpgradeState state)
    {
        var path = Path.Combine(workspace, "updates", "upgrade-state.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true);
    }
}
