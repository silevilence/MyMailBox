using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Chaos.NaCl;
using FlaUI.UIA3;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Updates;
using Application = FlaUI.Core.Application;

internal static class UpgradeUiChecks
{
    public static async Task MissingRuntime(string installer, string output)
    {
        var install = Path.Combine(output, "install");
        Directory.CreateDirectory(install);
        if (!string.Equals(Environment.UserName, "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此安装器验收使用一次性 Windows Sandbox。");
        var store = new SqliteMailStore(Path.Combine(output, "mail"));
        var raw = "Subject: preserved\r\n\r\noriginal"u8.ToArray();
        var id = await store.Import(SqliteMailStore.Inbox, raw, await new MyMailBox.Mail.MimeReader().Parse(raw));
        var backup = await new WorkspaceBackup(output,store).Create(Path.Combine(output, "original.mmbbackup"));
        var bytes = await File.ReadAllBytesAsync(installer);
        var seed = RandomNumberGenerator.GetBytes(32);
        Ed25519.KeyPairFromSeed(out var pub,out var priv,seed);
        CryptographicOperations.ZeroMemory(seed);
        var prepared = new PreparedUpdate("0.1.0",installer,bytes.Length,Convert.ToBase64String(Ed25519.Sign(bytes,priv)),
            Convert.ToHexString(SHA256.HashData(bytes)),Convert.ToHexString(SHA256.HashData(pub)));
        CryptographicOperations.ZeroMemory(priv);
        var job = new UpdateJob(prepared,install,output,backup,int.MaxValue,DateTime.UnixEpoch);
        var exit = await UpdateHandoff.Execute(job,Convert.ToBase64String(pub));
        var state = UpdateHandoff.ReadState(output);
        if (exit == 0 || state is not { State: "failed" } || !state.Message.Contains("1638") ||
            !state.Message.Contains(".NET 10 x64 Runtime") || state.Backup != backup || !File.Exists(backup) ||
            !(await store.Read(id)).SequenceEqual(raw))
            throw new Exception("安装器与更新助手的缺少运行时提示或数据保留不一致：" + state?.Message);
        Console.WriteLine("PASS real NSIS exit 1638 under update helper, precise x64 runtime guidance and original backup/mail retained");
    }

    public static void Run(string executable, string installer, string output) =>
        RunAsync(executable, installer, output).GetAwaiter().GetResult();

    private static async Task RunAsync(string executable, string installer, string output)
    {
        Directory.CreateDirectory(output);
        // Let the installed baseline create the profile it actually supports.
        var workspace = Path.Combine(output, "upgrade-workspace");
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
        var tasks = new MailAutomationService(store, new(store, mail), mail);
        var taskTarget = await mail.Create(SqliteMailStore.LocalAccount, null, "升级任务副本");
        var plan = await tasks.Save(new AutomationPlan(Guid.NewGuid(), 0, "升级前已执行的复制任务",
            new(SqliteMailStore.LocalAccount, SqliteMailStore.Inbox, "", "exists=1", 0, 0), true)
        {
            Target = new(SqliteMailStore.LocalAccount, SqliteMailStore.Inbox, false, "", null, null, 0, 0, true, "", SearchOrder.Oldest, 20, 0, null),
            Actions = [new(TaskActionKind.Copy, taskTarget)]
        });
        var execution = await tasks.Start(plan.Id, "manual/upgrade-fixture");
        execution = await tasks.Advance(execution.Id);
        if (execution.State != "done" || (await store.List(taskTarget, 0, 10)).Items.Count != 1)
            throw new Exception("升级前未能建立已执行的自动化回执。");
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
            var resumedTasks = new MailAutomationService(reopened, new(reopened, newMail), newMail);
            var retainedPlan = (await resumedTasks.Plans()).Single(p => p.Id == plan.Id);
            var retainedRun = (await resumedTasks.Runs(plan.Id)).Single();
            await resumedTasks.Advance(retainedRun.Id);
            if (retainedPlan.Actions.Single().Destination != taskTarget || retainedRun.Id != execution.Id || retainedRun.State != "done" ||
                (await reopened.List(taskTarget, 0, 10)).Items.Count != 1)
                throw new Exception("升级后自动化引用变化、丢失执行记录或重复复制邮件。");
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
            File.WriteAllText(Path.Combine(output, "upgrade-result.txt"), "PASS signed actual NSIS 0.1.0 -> 0.1.1, app shutdown, helper verification, automatic backup, restart acknowledgement and mail, identity, archived MIME, live draft, receive rule, completed automation copy+run receipts, plugin data and no duplicate SMTP submission after restart\n" + JsonSerializer.Serialize(state));
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
