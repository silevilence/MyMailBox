using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using MyMailBox.Mail;
using Application = FlaUI.Core.Application;

internal static class GraphUiChecks
{
    public static void Run()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var output = Path.Combine(repo, "artifacts/graph-acceptance/ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        var workspace = Path.Combine(output, "workspace"); var store = new SqliteMailStore(Path.Combine(workspace, "mail"));
        new AccountService(store).Save(new(null, "个人测试账户", "outlook-com", "synthetic@outlook.com")).GetAwaiter().GetResult();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MYMAILBOX_UI_EXE") ?? Path.Combine(repo, "artifacts/publish/MyMailBox.Desktop.exe")) { UseShellExecute = false };
        start.Environment["MYMAILBOX_HOME"] = workspace;
        using var automation = new UIA3Automation(); using var app = Application.Launch(start);
        var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15)) ?? throw new Exception("No desktop window"); var ui = new Ui(window);
        try
        {
            ui.Find("nav-diagnostics"); // Last startup registration; earlier navigation buttons are replaced while loading.
            ui.Click("nav-accounts/graph"); ui.Status("graph-status", "登录微软账户");
            if (window.FindFirstDescendant(cf => cf.ByAutomationId("graph-client")) is not null || window.FindFirstDescendant(cf => cf.ByAutomationId("graph-save")) is not null) throw new Exception("User login still requires application registration configuration");
            if (!ui.Find("graph-login").IsEnabled) throw new Exception("Direct login is unavailable");
            // The enabled action opens the real Microsoft browser; local UI checks do not trigger personal authorization.
            ui.Click("nav-accounts/manage"); ui.Click("nav-accounts/graph"); ui.Status("graph-status", "登录微软账户");
            ui.Click("graph-root"); ui.Status("graph-status", "尚未连接");
            ui.Click("graph-read"); ui.Status("graph-status", "请选择目录");
            ui.Click("graph-download"); ui.Status("graph-status", "请选择邮件");
            ui.Click("graph-disconnect"); ui.Status("graph-status", "已清除");
            window.Focus(); Capture.Element(window).ToFile(Path.Combine(output, "graph-settings.png"));
            ui.Click("nav-accounts/protocols"); ui.Status("protocol-status", "账户");
            window.Focus(); Capture.Element(window).ToFile(Path.Combine(output, "protocol-isolation.png"));
            var process = Process.GetProcessById(app.ProcessId); window.Close();
            if (!process.WaitForExit(15000)) throw new Exception("Graph UI did not exit cleanly");
            File.WriteAllText(Path.Combine(output, "result.txt"), "PASS direct-login UI/no Client ID input/reopen/unsigned recovery/disconnect/provider isolation; real Graph login and cloud browser remain untested");
            Console.WriteLine("PASS Graph desktop configuration UI: " + output);
        }
        catch { Capture.Element(window).ToFile(Path.Combine(output, "failure.png")); try { window.Close(); } catch { } throw; }
    }
}
