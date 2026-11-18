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
    public static void Run(string executable, string installer, string output) =>
        RunAsync(executable, installer, output).GetAwaiter().GetResult();

    private static async Task RunAsync(string executable, string installer, string output)
    {
        Directory.CreateDirectory(output);
        var workspace = Path.Combine(output, "upgrade-workspace"); DefaultProfile.Ensure(Path.Combine(workspace, "profiles/default"));
        var store = new SqliteMailStore(Path.Combine(workspace, "mail"));
        var raw = "From: test@example.test\r\nTo: reader@example.test\r\nSubject: Upgrade preservation\r\n\r\nKeep this raw message."u8.ToArray();
        var messageId = store.Import(SqliteMailStore.Inbox, raw, new MyMailBox.Mail.MimeReader().Parse(raw).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        var accounts = new AccountService(store);
        var sender = accounts.Save(new(null,"升级发件身份","smtp","sender@localhost")).GetAwaiter().GetResult();
        var mail = new ProtocolMailService(store);
        await using var smtp = new ProtocolFaultServer(true) { DropAfterData = true, AcceptAll = true };
        var endpoint = new MailEndpoint("127.0.0.1",smtp.Port,"sender",CertificateSha256:smtp.Pin);
        mail.Save(sender.Id,new(Outgoing:endpoint),null,"fixture").GetAwaiter().GetResult();
        var uncertain = Guid.NewGuid();
        mail.Enqueue(uncertain,sender.Id,new("recipient@localhost","","","acknowledgement lost","body")).GetAwaiter().GetResult();
        if (mail.Deliver(uncertain).GetAwaiter().GetResult().State != "unknown" || smtp.Submissions != 1)
            throw new Exception("无法生成已投递但没有收到确认的真实 SMTP 记录。");
        smtp.DropAfterData = false;
        var archive = mail.Create(SqliteMailStore.LocalAccount,null,"升级归档").GetAwaiter().GetResult();
        var archived = store.Import(archive, raw, new MyMailBox.Mail.MimeReader().Parse(raw).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        var rules = new ReceiveRuleService(store,new(store,mail),mail);
        var rule = rules.Save(new(Guid.NewGuid(),0,"升级规则",SqliteMailStore.LocalAccount,SqliteMailStore.Inbox,
            "subject:sample","subject:private",[new(RuleActionKind.Move, Destination:archive)],2,true,true,false)).GetAwaiter().GetResult();
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
            var ui = new Ui(window); ui.Find("nav-diagnostics");
            ui.Click("nav-accounts/compose");ui.Text("compose-to","reader@example.test");
            ui.Text("compose-subject","升级前正在编辑的草稿");ui.Text("compose-body","保留编写中的正文");
            ui.Click("nav-updates/settings"); ui.Status("update-install-status", "校验通过"); ui.Click("update-install");
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
            var reopened = new SqliteMailStore(Path.Combine(workspace,"mail"));
            var newMail = new ProtocolMailService(reopened);
            var retained = new ReceiveRuleService(reopened,new(reopened,newMail),newMail).Dashboard().GetAwaiter().GetResult().Rules.Single(r => r.Id == rule.Id);
            var draft = new MailDraftStore(reopened).List(sender.Id).Single(d => d.Subject == "升级前正在编辑的草稿");
            if (draft.To != "reader@example.test" || !draft.Body.Plain.Contains("保留编写中的正文") ||
                !reopened.Read(archived).GetAwaiter().GetResult().SequenceEqual(raw) ||
                JsonSerializer.Serialize(rule) != JsonSerializer.Serialize(retained) ||
                !new AccountService(reopened).List().GetAwaiter().GetResult().Any(a => a.Id == sender.Id && a.Address == sender.Address))
                throw new Exception("升级后草稿、规则、归档原文或发件身份发生变化。");
            if (newMail.List(sender.Id,0,20).GetAwaiter().GetResult().Single(m => m.Id == uncertain).State != "unknown" ||
                newMail.Deliver(uncertain).GetAwaiter().GetResult().State != "unknown" || smtp.Submissions != 1)
                throw new Exception("已提交但缺少确认的邮件被重放或丢失。");
            File.WriteAllText(Path.Combine(output, "upgrade-result.txt"), "PASS signed actual NSIS 0.1.0 -> 0.1.1, app shutdown, helper verification, automatic backup, restart acknowledgement and mail, identity, archived MIME, live draft, receive rule, plugin data and no duplicate SMTP submission after restart\n" + JsonSerializer.Serialize(state));
            Console.WriteLine("PASS actual NSIS upgrade through desktop install action and helper; preserved draft/account/rule/archive/plugin data and backup, verified no replay after a real lost SMTP acknowledgement");
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
