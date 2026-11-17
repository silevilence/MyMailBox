using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Chaos.NaCl;
using MyMailBox.Contracts;
using MyMailBox.Runtime;
using MyMailBox.Updates;

internal static class UpdateInstallChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(PluginChecks.Repository, "artifacts/checks/update-install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Ed25519.KeyPairFromSeed(out var publicBytes, out var privateBytes, RandomNumberGenerator.GetBytes(32)); var key = Convert.ToBase64String(publicBytes);
        var installer = Path.Combine(root, "source.exe"); var bytes = RandomNumberGenerator.GetBytes(8192); await File.WriteAllBytesAsync(installer, bytes);
        var signature = Convert.ToBase64String(Ed25519.Sign(bytes, privateBytes));
        var cast = SignedAppCast.Create("0.2.0", new Uri(installer), bytes.Length, signature, "本地更新定向检查");
        var castPath = Path.Combine(root, "appcast.xml"); await File.WriteAllBytesAsync(castPath, cast);
        await File.WriteAllTextAsync(castPath + ".signature", Convert.ToBase64String(Ed25519.Sign(cast, privateBytes)));
        var release = new UpdateRelease("0.2.0", "notes", new Uri("https://github.com/silevilence/MyMailBox"), new("source.exe", new Uri(installer), bytes.Length), new("appcast.xml", new Uri(castPath), cast.Length), new("appcast.xml.signature", new Uri(castPath + ".signature"), 88));
        var downloads = Path.Combine(root, "downloads"); PreparedUpdate? queued = null;
        using (var packages = new UpdatePackages(downloads, key, "0.1.0", p => queued = p))
        {
            Check((await packages.Download(release)).State == UpdateInstallState.Ready, "signed package ready");
            await packages.RequestInstall(); Check(queued?.PluginSdk == 1, "signature-protected SDK information and queued handoff");
        }
        using (var restored = new UpdatePackages(downloads, key, "0.1.0", p => queued = p))
        {
            Check((await restored.Status()).State == UpdateInstallState.Ready, "restart revalidates ready package");
            await File.AppendAllTextAsync(queued!.File, "tamper");
            await PluginChecks.Reject(() => restored.RequestInstall(), "不正确");
            Check((await restored.Status()).State == UpdateInstallState.Failed, "last-moment tamper blocks install");
        }
        using (var noKey = new UpdatePackages(Path.Combine(root, "no-key"), "", "0.1.0", _ => throw new Exception("unexpected install")))
        { Check((await noKey.Status()).State == UpdateInstallState.NotConfigured, "missing trust key visible"); await PluginChecks.Reject(() => noKey.Download(release), "公钥"); }
        using (var badHash = new UpdatePackages(Path.Combine(root, "bad-hash"), key, "0.1.0", _ => throw new Exception("unexpected install")))
        { await PluginChecks.Reject(() => badHash.Download(release with { Installer = release.Installer with { Sha256 = new string('0', 64) } }), "SHA256"); await PluginChecks.Reject(() => badHash.RequestInstall(), "先下载"); }
        using (var mismatch = new UpdatePackages(Path.Combine(root, "mismatch"), key, "0.1.0", _ => throw new Exception("unexpected install")))
        { await PluginChecks.Reject(() => mismatch.Download(release with { Version = "0.3.0" }), "不一致"); }
        using (var upgraded = new UpdatePackages(downloads, key, "0.2.0", _ => throw new Exception("unexpected install")))
        { Check((await upgraded.Status()).State == UpdateInstallState.Failed, "reinstall and downgrade refused"); }
        await Cancellation(root, key, release);
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace); var data = Path.Combine(workspace, "user-data.txt"); await File.WriteAllTextAsync(data, "用户数据不可删除");
        var job = new UpdateJob(queued!, root, workspace, Path.Combine(root, "backup.zip"), int.MaxValue, DateTime.MinValue);
        Check(await UpdateHandoff.Execute(job, key) == 1, "helper independently rejects damaged installer");
        Check(UpdateHandoff.ReadState(workspace)?.State == "failed" && await File.ReadAllTextAsync(data) == "用户数据不可删除", "diagnostics and user data survive failed handoff");
        UpdateHandoff.WriteState(job, "awaiting-startup", "pending"); UpdateHandoff.MarkStartup(workspace, "0.2.0", false, "模拟迁移失败");
        Check(UpdateHandoff.ReadState(workspace) is { State: "failed" } failed && failed.Message.Contains("不自动回滚数据库") && failed.Backup == job.Backup, "migration failure has explicit independent restore path");
        UpdateHandoff.WriteState(job, "awaiting-startup", "pending"); UpdateHandoff.MarkStartup(workspace, "0.1.0", true);
        Check(UpdateHandoff.ReadState(workspace)?.State == "failed", "old executable cannot mark upgrade healthy");
        UpdateHandoff.WriteState(job, "awaiting-startup", "pending"); UpdateHandoff.MarkStartup(workspace, "0.2.0", true);
        Check(UpdateHandoff.ReadState(workspace)?.State == "completed", "successful startup acknowledgement");
        var pages = new Contributions(); await using var runtime = new PluginRuntime(pages); var saved = false;
        var plugin = new PluginDefinition(new("prepare", "1.0.0", 1, 1, PluginKind.Ordinary, "prepare"), PluginOrigin.Builtin, "", (ctx, _) =>
        { var host = ctx.Get<IPluginHost>("mailbox.host")!; ctx.Effect(() => host.AddUpdatePreparation(new("drafts", _ => { saved = true; return Task.CompletedTask; }))); });
        await runtime.Start(plugin, new() { InstanceId = "prepare" }); foreach (var p in pages.UpdatePreparations) await p.Save(CancellationToken.None);
        Check(saved, "plugin preparation hook saves before drain"); await runtime.Stop("prepare"); Check(pages.UpdatePreparations.Count == 0, "preparation hook removed on stop");
        CryptographicOperations.ZeroMemory(privateBytes);
        Console.WriteLine("PASS update install: signed version/SDK/assets, restart/tamper/hash/key/cancel guards, preparation lifecycle, failure preservation and startup/migration acknowledgements; actual NSIS upgrade remains consolidated");
    }
    private static async Task Cancellation(string root, string key, UpdateRelease release)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverStop = new CancellationTokenSource(); var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(serverStop.Token); using var reader = new StreamReader(client.GetStream());
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(serverStop.Token))) { }
            requested.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, serverStop.Token);
        });
        try
        {
            using var cancellation = new CancellationTokenSource(); using var packages = new UpdatePackages(Path.Combine(root, "cancel"), key, "0.1.0", _ => throw new Exception("unexpected install"));
            var url = new Uri($"http://127.0.0.1:{port}/appcast.xml");
            var pending = packages.Download(release with { AppCast = release.AppCast with { Url = url }, Signature = release.Signature with { Url = new Uri(url.AbsoluteUri + ".signature") } }, cancellation: cancellation.Token);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            await PluginChecks.Reject(() => pending, "canceled"); Check((await packages.Status()).State == UpdateInstallState.Failed, "in-flight cancellation never queues installer");
        }
        finally { serverStop.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } }
    }
    private static void Check(bool value, string message) => PluginChecks.Check(value, "update install: " + message);
}
