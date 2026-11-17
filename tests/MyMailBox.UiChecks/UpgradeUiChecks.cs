using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Chaos.NaCl;
using FlaUI.UIA3;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Updates;
using Application = FlaUI.Core.Application;

internal static class UpgradeUiChecks
{
    public static void Run(string executable, string installer, string output)
    {
        Directory.CreateDirectory(output);
        var workspace = Path.Combine(output, "upgrade-workspace"); DefaultProfile.Ensure(Path.Combine(workspace, "profiles/default"));
        var store = new SqliteMailStore(Path.Combine(workspace, "mail"));
        var raw = "From: test@example.test\r\nTo: reader@example.test\r\nSubject: Upgrade preservation\r\n\r\nKeep this raw message."u8.ToArray();
        var messageId = store.Import(SqliteMailStore.Inbox, raw, new MyMailBox.Mail.MimeReader().Parse(raw).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        var custom = Path.Combine(workspace, "plugin-data", "user-settings.txt"); Directory.CreateDirectory(Path.GetDirectoryName(custom)!); File.WriteAllText(custom, "用户配置保留");
        Ed25519.KeyPairFromSeed(out var publicBytes, out var privateBytes, RandomNumberGenerator.GetBytes(32));
        var key = Convert.ToBase64String(publicBytes);
        var bytes = File.ReadAllBytes(installer); var signature = Convert.ToBase64String(Ed25519.Sign(bytes, privateBytes));
        var cast = SignedAppCast.Create("0.1.1", new Uri(installer), bytes.Length, signature, "仅本地安装验收，不是正式发布");
        var castPath = Path.Combine(output, "appcast.xml"); File.WriteAllBytes(castPath, cast); File.WriteAllText(castPath + ".signature", Convert.ToBase64String(Ed25519.Sign(cast, privateBytes))); CryptographicOperations.ZeroMemory(privateBytes);
        var release = new UpdateRelease("0.1.1", "local acceptance", new Uri("https://github.com/silevilence/MyMailBox"),
            new(Path.GetFileName(installer), new Uri(installer), bytes.Length), new("appcast.xml", new Uri(castPath), cast.Length), new("appcast.xml.signature", new Uri(castPath + ".signature"), 88));
        using (var packages = new UpdatePackages(Path.Combine(workspace, "updates/downloads"), key, "0.1.0", _ => throw new Exception("Setup must not install")))
            if (packages.Download(release).GetAwaiter().GetResult().State != UpdateInstallState.Ready) throw new Exception("Actual NSIS package not prepared");
        // This temporary key exists only in the disposable sandbox installation, never in tracked release configuration.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, "update-source.json"), JsonSerializer.Serialize(new { repository = "silevilence/MyMailBox", publicKey = key }));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false }; start.Environment["MYMAILBOX_HOME"] = workspace;
        using var automation = new UIA3Automation(); using var app = Application.Launch(start);
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20)) ?? throw new Exception("No upgrade window");
            var ui = new Ui(window); ui.Click("nav-updates/settings"); ui.Status("update-install-status", "校验通过"); ui.Click("update-install");
            var deadline = DateTime.UtcNow.AddMinutes(3); UpgradeState? state;
            do
            {
                state = UpdateHandoff.ReadState(workspace);
                if (state?.State == "failed") throw new Exception(state.Message);
                if (state?.State == "completed") break;
                Thread.Sleep(300);
            } while (DateTime.UtcNow < deadline);
            if (state?.State != "completed" || !app.HasExited) throw new Exception("Upgrade did not exit/restart/acknowledge: " + state);
            if (!File.Exists(state.Backup) || !new SqliteMailStore(Path.Combine(workspace, "mail")).Read(messageId).GetAwaiter().GetResult().SequenceEqual(raw) || File.ReadAllText(custom) != "用户配置保留") throw new Exception("Upgrade failed to preserve data/backup");
            File.WriteAllText(Path.Combine(output, "upgrade-result.txt"), "PASS signed actual NSIS 0.1.0 -> 0.1.1, app shutdown, helper verification, automatic backup, restart acknowledgement and mail/plugin data preservation\n" + JsonSerializer.Serialize(state));
            Console.WriteLine("PASS actual NSIS upgrade through desktop install action and helper; preserved raw mail/configuration and independent backup");
        }
        finally
        {
            if (!app.HasExited) app.Kill();
            foreach (var process in Process.GetProcessesByName("MyMailBox.Desktop"))
            {
                using (process)
                {
                    if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) continue;
                    process.CloseMainWindow(); if (!process.WaitForExit(15000)) process.Kill();
                }
            }
        }
    }
}
