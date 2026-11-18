using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using Application = FlaUI.Core.Application;

internal static class ProtocolUiChecks
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool PostMessage(nint hwnd,uint msg,nint wParam,nint lParam);
    public static void Run()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var output = Path.Combine(repo, "artifacts/protocol-acceptance/ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        var workspace = Path.Combine(output, "workspace"); var store = new SqliteMailStore(Path.Combine(workspace, "mail"));
        var accounts = new AccountService(store); var service = new ProtocolMailService(store);
        var alice = accounts.Save(new(null, "Alice IMAP SMTP", "imap-smtp", "alice@localhost", Order: 1)).GetAwaiter().GetResult();
        var bob = accounts.Save(new(null, "Bob POP", "pop3", "bob@localhost", Order: 2)).GetAwaiter().GetResult();
        var pin = File.ReadAllText(Path.Combine(repo, "artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
        var endpoint = new MailEndpoint("127.0.0.1", 3993, "alice@localhost", CertificateSha256: pin);
        service.Save(alice.Id, new(endpoint, endpoint with { Port = 3465 }), "local-alice", "local-alice").GetAwaiter().GetResult();
        service.Save(bob.Id, new(endpoint with { Port = 3995, User = "bob@localhost" }), "local-bob", null).GetAwaiter().GetResult();
        var actionSource = service.Create(SqliteMailStore.LocalAccount,null,"界面动作来源").GetAwaiter().GetResult();
        var sample = System.Text.Encoding.UTF8.GetBytes("From: fixture@example.test\r\nSubject: Local action\r\n\r\nFixture");
        store.Import(actionSource,sample,new MyMailBox.Mail.MimeReader().Parse(sample).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        var archiveRoot=service.Create(SqliteMailStore.LocalAccount,null,"界面归档").GetAwaiter().GetResult();
        var archiveInput=service.Create(SqliteMailStore.LocalAccount,null,"归档来源").GetAwaiter().GetResult();
        var old=System.Text.Encoding.UTF8.GetBytes("Subject: Archive fixture\r\nDate: Tue, 01 Apr 2025 12:00:00 +0000\r\n\r\nArchive");
        store.Import(archiveInput,old,new MyMailBox.Mail.MimeReader().Parse(old).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        var auto=new MailAutomationService(store,new MailSearchService(store,service),service);
        var archive=auto.Save(new AutomationPlan(Guid.NewGuid(),0,"旧邮件测试",new(SqliteMailStore.LocalAccount,archiveInput,"","count>=1",0,0),true)
        {
            Target=new(SqliteMailStore.LocalAccount,archiveInput,false,"",null,null,30,0,true,"保留",SearchOrder.Oldest,10,0,null),
            Archive=new(SqliteMailStore.LocalAccount,archiveRoot,false,540,"UTC")
        }).GetAwaiter().GetResult();
        var systemPlan=auto.Save(archive with{Id=Guid.NewGuid(),Version=0,Name="恢复事件",Archive=null,Actions=[new(TaskActionKind.Notify,Argument:"已收到恢复事件")],Triggers=[new("resume")]}).GetAwaiter().GetResult();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MYMAILBOX_UI_EXE") ?? Path.Combine(repo, "artifacts/publish/MyMailBox.Desktop.exe")) { UseShellExecute = false };
        start.Environment["MYMAILBOX_HOME"] = workspace;
        using var automation = new UIA3Automation(); using var app = Application.Launch(start);
        var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15)) ?? throw new Exception("No desktop window"); var ui = new Ui(window);
        void Status(string expected) => ui.Status("protocol-status", expected);
        void Tab(int index) { ui.Find("protocol-tabs").FindAllChildren(cf => cf.ByControlType(ControlType.TabItem))[index].AsTabItem().Select(); Thread.Sleep(150); }
        try
        {
            ui.Click("nav-accounts/protocols"); Status("已载入");
            ui.Text("incoming-port", "0"); ui.Click("protocol-save"); Status("端口");
            ui.Text("incoming-port", "3993"); ui.Click("protocol-save"); Status("配置已保存");
            ui.Click("nav-accounts/manage"); ui.Click("nav-accounts/protocols"); Status("已载入");
            if (ui.Find("incoming-port").AsTextBox().Text != "3993" || ui.Find("incoming-secret").AsTextBox().Text.Length != 0) throw new Exception("Reopen lost settings or exposed secret");
            Tab(1); ui.Click("imap-connect"); Status("连接成功"); ui.Click("imap-read"); Status("第 1 页");
            ui.Click("imap-next"); Status("第 2 页"); ui.Click("imap-previous"); Status("第 1 页");
            Capture.Element(window).ToFile(Path.Combine(output, "01-imap.png"));
            Tab(4); ui.Click("sync-once"); ui.Status("sync-status", "同步完成");
            ui.Find("sync-messages").AsListBox().Select(0); ui.Click("sync-open");
            if (!SpinWait.SpinUntil(() => ui.Find("sync-body").AsTextBox().Text.Contains("真实 SMTP"), 10000))
                throw new Exception("Downloaded IMAP message was not readable");
            ui.Expand("离线修改（下次同步时应用）"); ui.Click("offline-Star"); ui.Status("offline-status","待同步 1");
            ui.Click("sync-once"); ui.Status("offline-status","已确认 1");
            ui.Click("sync-watch"); ui.Status("sync-status", "等待新邮件"); ui.Click("sync-stop"); ui.Status("sync-status", "同步已停止");
            Tab(0); ui.Text("incoming-secret", "wrong-ui-password"); ui.Click("protocol-save"); Status("配置已保存");
            Tab(1); ui.Click("imap-connect"); Status("认证失败");
            Tab(0); ui.Text("incoming-secret", "local-alice"); ui.Click("protocol-save"); Status("配置已保存");
            Tab(1); ui.Click("imap-connect"); Status("连接成功");
            Tab(2); ui.Text("smtp-to", "invalid address"); ui.Click("smtp-send"); Status("地址无效");
            ui.Text("smtp-to", "bob@localhost"); ui.Text("smtp-cc", "alice@localhost"); ui.Text("smtp-bcc", "bob@localhost");
            ui.Text("smtp-subject", "UI-真实测试-中文"); ui.Text("smtp-body", "通过桌面发送的正文"); ui.Click("smtp-send"); Status("服务器已接收");
            if (ui.Find("smtp-send").IsEnabled) throw new Exception("Completed send can be accidentally repeated");
            Capture.Element(window).ToFile(Path.Combine(output, "02-smtp.png"));
            Tab(5); ui.Click("outbox-refresh"); ui.Status("outbox-detail", "服务器已接收");
            if (ui.Find("outbox-retry").IsEnabled) throw new Exception("Delivered mail can be retried");
            ui.Select("protocol-account", 1); Status("已载入"); Tab(3); ui.Click("pop-receive"); Status("本批新增");
            ui.Click("pop-receive"); Status("本批新增 0");
            Capture.Element(window).ToFile(Path.Combine(output, "03-pop.png"));
            var folder = store.Folders().GetAwaiter().GetResult().Single(f => f.AccountId == bob.Id);
            var messages = store.List(folder.Id, 0, 100).GetAwaiter().GetResult().Items;
            if (!messages.Any(m => m.Subject == "UI-真实测试-中文")) throw new Exception("UI POP did not persist SMTP message");
            ui.Click("nav-accounts/organize");
            var orgFolders = ui.Find("org-folder").AsComboBox();
            if (!SpinWait.SpinUntil(() => orgFolders.Items.Length > 0, 5000)) throw new Exception("Organization folders missing");
            orgFolders.Expand(); orgFolders.Items.Single(i => i.Text.Contains("界面动作来源")).Select(); orgFolders.Collapse();
            ui.Status("org-status", "第 1 页"); ui.Find("org-messages").AsListBox().Select(0);
            ui.Expand("移动、复制与删除"); ui.Click("org-action-Trash"); ui.Status("org-status", "已移入垃圾箱");
            ui.Click("org-undo"); ui.Status("org-status", "已撤销 1");
            var orgAccounts = ui.Find("org-account").AsComboBox();
            orgAccounts.Expand(); orgAccounts.Items.Single(item => item.Text == "Alice IMAP SMTP").Select(); orgAccounts.Collapse();
            ui.Click("org-refresh"); ui.Status("org-status", "第 1 页");
            var marked = ui.Find("org-messages").AsListBox(); marked.Select(0);
            ui.Text("org-label", "验收"); ui.Click("org-add-label"); ui.Status("org-status", "成功 1");
            if (marked.SelectedItems.Length != 1) throw new Exception("Marking discarded selection");
            ui.Expand("跨邮箱归档"); ui.Click("transfer-copy"); ui.Status("org-status", "复制完成");
            ui.Status("transfer-detail", "完整性已验证");
            if (ui.Find("transfer-resume").IsEnabled) throw new Exception("Completed transfer offered a retry");
            if (ui.Find("transfer-history").AsListBox().Items.Length == 0) throw new Exception("Archive history missing");
            ui.Click("nav-accounts/inbox"); ui.Select("unified-filter", 1);
            ui.Click("unified-auto"); ui.Status("unified-status", "接收完成");
            ui.Find("unified-list").AsListBox().Select(0); ui.Click("unified-open");
            if (!SpinWait.SpinUntil(() => ui.Find("unified-body").AsTextBox().Text.Length > 0, 10000)) throw new Exception("Unified body missing");
            ui.Click("unified-auto"); ui.Status("unified-status", "已暂停");
            ui.Click("nav-accounts/search"); ui.Text("search-value","Local action"); ui.Click("search-run"); ui.Status("search-status","第 1 页");
            var cancelSearch = ui.Find("search-cancel").AsButton(); var searchStatus = ui.Find("search-status");
            var feedback = System.Diagnostics.Stopwatch.StartNew(); cancelSearch.Invoke();
            if (!SpinWait.SpinUntil(() => searchStatus.Name.Contains("已取消搜索"), 100) || feedback.ElapsedMilliseconds >= 100)
                throw new Exception($"取消搜索反馈超出 100 ms：{feedback.ElapsedMilliseconds} ms");
            Console.WriteLine($"PASS cancel feedback via UI Automation: {feedback.ElapsedMilliseconds} ms");
            ui.Find("search-results").AsListBox().Select(0); ui.Click("search-open");
            if (!SpinWait.SpinUntil(()=>ui.Find("search-body").AsTextBox().Text.Contains("Fixture"),5000)) throw new Exception("Search result not readable");
            ui.Click("search-locate"); ui.Click("search-back"); ui.Status("search-status","第 1 页");
            ui.Click("nav-accounts/compose");ui.Text("compose-subject","界面草稿");ui.Text("compose-body","editable body");
            ui.Click("compose-rich");ui.Status("compose-status","富文本编辑器已就绪");
            ui.Click("compose-plain");if(ui.Find("compose-body").AsTextBox().Text!="editable body")throw new Exception("Rich/plain draft handoff lost body");
            ui.Click("nav-accounts/rules"); ui.Text("rule-name","界面试运行");ui.Text("rule-condition","subject:Local");
            ui.Click("rule-add-action");ui.Click("rule-preview");ui.Status("rule-status","命中");
            if(!PostMessage(window.Properties.NativeWindowHandle.Value,0x218,18,0))throw new Exception("Could not post resume to isolated application window");
            if(!SpinWait.SpinUntil(()=>auto.Runs(systemPlan.Id).GetAwaiter().GetResult().Any(r=>r.State=="done"),15000))throw new Exception("Windows resume event did not trigger automation");
            ui.Click("nav-accounts/automation");ui.Select("auto-plans",0);ui.Click("auto-load");ui.Expand("动作、时间与执行记录");
            ui.Click("auto-archive-preview");ui.Status("auto-status","2025/04");ui.Click("auto-start");
            if(!SpinWait.SpinUntil(()=>auto.Runs(archive.Id).GetAwaiter().GetResult().Any(r=>r.State=="done"),15000))throw new Exception("UI archive failed to finish");
            ui.Click("auto-history-refresh");ui.Status("auto-status","完成 1");
            ui.Click("nav-local-mail/inbox");
            Capture.Element(window).ToFile(Path.Combine(output, "04-local.png"));
            var process = Process.GetProcessById(app.ProcessId); window.Close();
            if (!process.WaitForExit(15000)) throw new Exception("Protocol UI did not shut down cleanly");
            File.WriteAllText(Path.Combine(output, "result.txt"), "PASS actual desktop: save/reopen/validation, IMAP folder+paging, authentication failure+recovery, SMTP To/Cc/Bcc, repeat-send prevention, POP persistence+dedupe, shutdown");
            Console.WriteLine("PASS protocol UI: " + output);
        }
        catch
        {
            Capture.Element(window).ToFile(Path.Combine(output, "failure.png"));
            File.WriteAllText(Path.Combine(output, "tree.txt"), window.ToString());
            try { window.Close(); } catch { }
            throw;
        }
    }
}
