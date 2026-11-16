using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;
using Application = FlaUI.Core.Application;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var output = Path.Combine(repo, "artifacts/ui-acceptance"); Directory.CreateDirectory(output);
        var workspace = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(output, "workspace-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(workspace, "profiles/default"); DefaultProfile.Ensure(profile);
        if (args.Length > 0)
        {
            ConfigurationFiles.Save(Path.Combine(profile, "core.json"), DefaultProfile.Core);
            var existing = ConfigurationFiles.Read(Path.Combine(profile, "plugins.json"));
            ConfigurationFiles.Save(Path.Combine(profile, "plugins.json"), existing with { Instances = existing.Instances.Where(p => p.Id is not "alternative" and not "native").ToArray() });
        }
        var exe = Path.Combine(repo, "artifacts/publish/MyMailBox.Desktop.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false }; start.Environment["MYMAILBOX_HOME"] = workspace;
        using var automation = new UIA3Automation();
        using var app = Application.Launch(start);
        Window? window = null;
        try
        {
            window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15));
            if (window is null) throw new Exception("No desktop window");
            var ui = new Ui(window);
            ui.Find("nav-welcome/welcome");
            if (args.Length > 1 && args[1] == "finish")
            {
                ui.Click("nav-plugin-settings"); ui.Text("instance-id", "example"); ui.Text("greeting", "设置页已更新");
                ui.Click("save-plugin-settings"); ui.Result("已保存并生效");
                ui.Click("nav-example/example"); ui.Click("ping"); ui.Result("设置页已更新");
                ui.Click("nav-plugin-manager"); ui.Text("plugin-id", "example"); ui.Text("plugin-version", "1.0.0");
                ui.Click("remove-package"); ui.Result("指定版本已移除");
                if (!Directory.Exists(Path.Combine(workspace, "plugins/example/2.0.0"))) throw new Exception("Removal affected another version");
                ui.Text("plugin-version", "2.0.0"); ui.Text("managed-instance", "example");
                ui.Click("remove-instance"); ui.Result("已停用"); ui.Gone("nav-example/example");
                ui.Click("remove-package"); ui.Result("指定版本已移除");
                ui.Click("nav-diagnostics"); ui.Click("export-diagnostics"); ui.Result("诊断已导出");
                var finishProcess = Process.GetProcessById(app.ProcessId); window.Close();
                if (!finishProcess.WaitForExit(15000)) throw new Exception("Final shutdown failed");
                File.WriteAllText(Path.Combine(output, "finish-result.txt"), "PASS settings save, live plugin update, instance removal, version removal isolation, diagnostic export, shutdown");
                Console.WriteLine("PASS UI settings + live config + instance/package removal + version isolation + final shutdown");
                return;
            }
            if (args.Length == 0)
            {
            Capture.Element(window).ToFile(Path.Combine(output, "01-welcome.png"));
            ui.Click("nav-prototype/read"); ui.Click("read-message"); ui.Result("工作");
            ui.Select("read-account", 1); ui.Click("read-message"); ui.Result("alex@personal.example");
            ui.Click("read-empty"); ui.Result("没有邮件"); ui.Click("read-error"); ui.Result("加载失败"); ui.Click("read-retry"); ui.Result("本周安排");
            ui.Click("nav-prototype/compose"); ui.Select("send-account", 1); ui.Click("save-draft"); ui.Result("alex@personal.example");
            ui.Click("restore-draft"); ui.Result("alex@personal.example");
            ui.Find("recipient").Focus(); Keyboard.Type(VirtualKeyShort.TAB); Thread.Sleep(100);
            if (!ui.Find("subject").Properties.HasKeyboardFocus.Value) throw new Exception("Keyboard field traversal failed");
            ui.Click("nav-prototype/automation"); ui.Click("preview-plan"); ui.Result("处理来源：个人");
            Capture.Element(window).ToFile(Path.Combine(output, "02-automation.png"));
            ui.Click("fail-plan"); ui.Result("来源全部保留"); ui.Click("nav-prototype/recovery"); ui.Click("resume-plan"); ui.Result("仅继续第 3 封");
            Console.WriteLine("PASS UI prototype + keyboard");

            ui.Click("nav-plugin-manager");
            foreach (var name in new[] { "example-1.0.0", "example-2.0.0", "alternative-selector-1.0.0", "native-1.0.0" })
            {
                ui.Text("package-path", Path.Combine(repo, "artifacts/packages", name + ".zip"));
                ui.Click("inspect-package"); ui.Result("完整性与 SDK 检查通过"); ui.Click("install-package"); ui.Result("已安装");
            }
            ui.Text("plugin-id", "example"); ui.Text("plugin-version", "1.0.0"); ui.Text("managed-instance", "example");
            ui.Click("enable-plugin"); ui.Result("实例已启用"); ui.Click("nav-example/example"); ui.Click("ping"); ui.Result("私有依赖");
            ui.Click("nav-plugin-manager"); ui.Click("start-operation"); ui.Result("模拟任务已接单"); ui.Text("plugin-version", "2.0.0");
            ui.Click("upgrade-plugin"); ui.Result("新版本已启用");
            ui.Text("plugin-version", "1.0.0"); ui.Select("fail-initialization", 1); ui.Click("upgrade-plugin"); ui.Result("上次有效");
            ui.Click("list-plugins"); ui.Result("example → example@2.0.0");
            Console.WriteLine("PASS UI install + in-flight upgrade + rollback");
            }

            var pluginsPath = Path.Combine(profile, "plugins.json");
            var valid = ConfigurationFiles.Read(pluginsPath);
            File.WriteAllText(pluginsPath, "{partial"); ui.Notice("未生效"); ui.Find("nav-example/example");
            ConfigurationFiles.Save(pluginsPath, valid); ui.Notice("已生效");
            File.WriteAllText(Path.Combine(profile, "core.json"), "{broken");
            ui.Find("nav-example/example");
            Console.WriteLine("PASS UI file error recovery + core snapshot");

            // Thirty complete UI registration/selection/removal rounds use the same initialized app.
            var withoutSelector = valid.Instances.Where(p => p.Plugin != "selector").ToArray();
            for (var i = 0; i < 30; i++)
            {
                ConfigurationFiles.Save(pluginsPath, new(1, withoutSelector.Append(new PluginInstance("alternative", "alternative-selector", "1.0.0")).ToArray()));
                ui.Click("nav-alternative/selector"); ui.Click("confirm-selection"); ui.Result("personal");
                ui.Click("nav-plugin-manager");
                ConfigurationFiles.Save(pluginsPath, new(1, withoutSelector));
                ui.Gone("nav-alternative/selector");
                ui.Click("list-plugins"); ui.Result("alternative：已停用并释放");
                if ((i + 1) % 10 == 0) Console.WriteLine($"UI selector rounds: {i + 1}/30");
            }
            Console.WriteLine("PASS UI 30 navigation/unload selector rounds");

            // Native controls are an explicitly separate compatibility experiment.
            var nativeConfig = new PluginConfiguration(1, withoutSelector.Append(new PluginInstance("native", "native", "1.0.0", Grants: ["ui.native"])).ToArray());
            ConfigurationFiles.Save(pluginsPath, nativeConfig);
            ui.Click("nav-native/native"); Thread.Sleep(700);
            Capture.Element(window).ToFile(Path.Combine(output, "03-native.png"));
            ui.Click("nav-plugin-manager");
            ConfigurationFiles.Save(pluginsPath, new(1, withoutSelector));
            ui.Gone("nav-native/native");
            Thread.Sleep(9000);
            ui.Click("list-plugins");
            var nativeStatus = ui.Find("action-result").Name;
            if (!nativeStatus.Contains("native：已停用并释放") && !nativeStatus.Contains("资源/程序集释放失败")) throw new Exception("Native unload not diagnosed");
            File.WriteAllText(Path.Combine(output, "native-result.txt"), nativeStatus);
            Console.WriteLine(nativeStatus.Contains("资源/程序集释放失败") ? "PASS UI native retention explicitly reported (advanced controls require restart)" : "PASS UI native animation/resources unload");

            ui.Click("nav-diagnostics"); ui.Click("view-permissions"); ui.Result("声明"); ui.Click("export-diagnostics"); ui.Result("诊断已导出");
            if (!Directory.EnumerateFiles(Path.Combine(workspace, "exports"), "*.json").Any()) throw new Exception("No exported diagnostics");
            Capture.Element(window).ToFile(Path.Combine(output, "04-diagnostics.png"));
            var process = Process.GetProcessById(app.ProcessId);
            window.Close();
            if (!process.WaitForExit(15000)) throw new Exception("Application did not close cleanly");
            Console.WriteLine("PASS UI diagnostic export + clean shutdown");

            // A restart is necessary to verify core configuration changes are startup-only.
            using var recoveryApp = Application.Launch(start);
            try
            {
                var recoveryWindow = recoveryApp.GetMainWindow(automation, TimeSpan.FromSeconds(15)) ?? throw new Exception("No recovery window");
                var recovery = new Ui(recoveryWindow); recovery.Click("nav-recovery"); recovery.Find("safe-default");
                Capture.Element(recoveryWindow).ToFile(Path.Combine(output, "05-recovery.png"));
                recovery.Click("safe-default"); recovery.Result("重新启动");
                if (!Directory.EnumerateFiles(profile, "core.json.backup-*").Any()) throw new Exception("Recovery overwrote without backup");
                var recoveryProcess = Process.GetProcessById(recoveryApp.ProcessId);
                recoveryWindow.Close(); if (!recoveryProcess.WaitForExit(10000)) throw new Exception("Recovery app failed to close");
            }
            finally { if (!recoveryApp.HasExited) recoveryApp.Kill(); }
            Console.WriteLine("PASS UI safe startup + backup repair");
            File.WriteAllText(Path.Combine(output, "result.txt"), "PASS\nWorkspace: " + workspace + "\n" + nativeStatus);
        }
        catch
        {
            if (window is not null && !app.HasExited)
            {
                Capture.Element(window).ToFile(Path.Combine(output, "failure.png"));
                Console.Error.WriteLine("NOTICE: " + window.FindFirstDescendant(cf => cf.ByAutomationId("global-notice"))?.Name);
                Console.Error.WriteLine("RESULT: " + window.FindFirstDescendant(cf => cf.ByAutomationId("action-result"))?.Name);
            }
            throw;
        }
        finally { if (!app.HasExited) app.Kill(); }
    }
}

internal sealed class Ui(Window window)
{
    public AutomationElement Find(string id) => Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), e => e is not null, id)!;
    public void Click(string id) { Find(id).AsButton().Invoke(); Thread.Sleep(100); }
    public void Text(string id, string value) => Find(id).AsTextBox().Text = value;
    public void Select(string id, int index) => Find(id).AsComboBox().Select(index);
    public void Result(string value) => Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("action-result"))?.Name, text => text?.Contains(value) == true, "result: " + value);
    public void Notice(string value) => Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId("global-notice"))?.Name, text => text?.Contains(value) == true, "notice: " + value);
    public void Gone(string id) => Wait(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), e => e is null, "gone: " + id);
    private static T Wait<T>(Func<T> read, Func<T, bool> accept, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20); T value;
        do { value = read(); if (accept(value)) return value; Thread.Sleep(80); } while (DateTime.UtcNow < deadline);
        throw new Exception("UI timeout " + description + "; actual=" + value);
    }
}
