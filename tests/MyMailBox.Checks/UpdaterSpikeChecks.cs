using System.Security.Cryptography;
using Chaos.NaCl;
using MyMailBox.Updates;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;
using NuGet.Versioning;

internal static class UpdaterSpikeChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(PluginChecks.Repository, "artifacts/checks/updater-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        // Ephemeral demonstration key, never a production release key; private bytes stay in memory.
        Ed25519.KeyPairFromSeed(out var publicBytes, out var privateBytes, RandomNumberGenerator.GetBytes(32));
        var publicKey = Convert.ToBase64String(publicBytes);
        var payload = RandomNumberGenerator.GetBytes(4096);
        var installer = Path.Combine(root, "MyMailBox-V0.2.0-win-x64-setup.exe"); await File.WriteAllBytesAsync(installer, payload);
        var signature = Convert.ToBase64String(Ed25519.Sign(payload, privateBytes));
        var cast = SignedAppCast.Create("V0.2.0", new Uri(installer), payload.Length, signature, "签名测试 <中文> & 安装器边界");
        var castPath = Path.Combine(root, "appcast.xml"); await File.WriteAllBytesAsync(castPath, cast);
        await File.WriteAllTextAsync(castPath + ".signature", Convert.ToBase64String(Ed25519.Sign(cast, privateBytes)));
        using var sparkle = SparkleFactory.Create(new Uri(castPath), publicKey, "v0.1.0", Path.Combine(root, "downloads"));
        var info = await sparkle.CheckForUpdatesQuietly();
        Check(info.Status == UpdateStatus.UpdateAvailable && info.Updates.Single().Version == "0.2.0", "signed appcast check");
        var downloaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sparkle.DownloadFinished += (_, path) => downloaded.TrySetResult(path);
        sparkle.DownloadHadError += (_, _, error) => downloaded.TrySetException(error);
        sparkle.DownloadedFileIsCorrupt += (_, _) => downloaded.TrySetException(new Exception("corrupt download"));
        await sparkle.InitAndBeginDownload(info.Updates.Single());
        var saved = await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check((await File.ReadAllBytesAsync(saved)).SequenceEqual(payload), "download bytes preserved");
        var verifier = new Ed25519Checker(SecurityMode.Strict, publicKey, publicKeyFile: null);
        Check(verifier.VerifySignatureOfFile(signature, saved) == ValidationResult.Valid, "strict installer signature");
        var reused = 0; sparkle.DownloadStarted += (_, _) => reused++;
        await sparkle.InitAndBeginDownload(info.Updates.Single());
        Check(reused == 0, "validated local payload reused");
        await File.AppendAllTextAsync(saved, "tamper");
        Check(verifier.VerifySignatureOfFile(signature, saved) == ValidationResult.Invalid, "tampered installer refused");
        var called = false;
        System.ComponentModel.CancelEventHandler veto = (_, e) => { called = true; e.Cancel = true; };
        sparkle.PreparingToExit += veto;
        await sparkle.InstallUpdate(info.Updates.Single(), saved);
        Check(called, "host can prevent installation while work remains");
        sparkle.PreparingToExit -= veto;
        var refused = false;
        sparkle.InstallUpdateFailed += (reason, _) => { refused = reason == InstallUpdateFailureReason.InvalidSignature; return true; };
        await sparkle.InstallUpdate(info.Updates.Single(), saved);
        Check(refused, "installer entry refuses corrupted payload without launching");
        await File.AppendAllTextAsync(castPath, "tamper");
        Check((await sparkle.CheckForUpdatesQuietly()).Status == UpdateStatus.CouldNotDetermine, "bad appcast is failure, not up-to-date");
        foreach (var invalid in new[] { "1.0", "1.0.0.1", "01.0.0", "1.0.0-01", "Vbad" }) Check(ReleaseVersion.Parse(invalid) is null, "invalid semver: " + invalid);
        Check(VersionComparer.VersionRelease.Compare(ReleaseVersion.Parse("1.0.0+one"), ReleaseVersion.Parse("1.0.0+two")) == 0, "build metadata ignored");
        Check(VersionComparer.VersionRelease.Compare(ReleaseVersion.Parse("1.0.0-beta.10"), ReleaseVersion.Parse("1.0.0-beta.2")) > 0, "numeric prerelease order");
        CryptographicOperations.ZeroMemory(privateBytes);
        Console.WriteLine("PASS updater spike: NetSparkle 3.1.0 strict signed appcast/download, local reuse, corruption refusal, host exit veto, SemVer adapter; real NSIS install reserved for consolidated acceptance");
    }
    private static void Check(bool value, string message) => PluginChecks.Check(value, "updater: " + message);
}
