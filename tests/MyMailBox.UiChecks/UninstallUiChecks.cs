using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using CheckBox = FlaUI.Core.AutomationElements.CheckBox;
using Application = FlaUI.Core.Application;

internal static class UninstallUiChecks
{
    public static void Run(string executable, string defaultWorkspace)
    {
        // Only invoked inside the disposable validation sandbox after explicit fixture setup.
        if (!File.Exists(Path.Combine(defaultWorkspace, "preserve.txt"))) throw new Exception("Missing disposable workspace marker");
        using var automation = new UIA3Automation();
        using var app = Application.Launch(new ProcessStartInfo(executable, "_?=" + Path.GetDirectoryName(executable)) { UseShellExecute = false });
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15)) ?? throw new Exception("No uninstaller window");
            window.FindFirstDescendant(cf => cf.ByAutomationId("1"))!.AsButton().Invoke();
            var deadline = DateTime.UtcNow.AddSeconds(15); CheckBox? check = null;
            while (DateTime.UtcNow < deadline)
            {
                check = window.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).FirstOrDefault(e => e.Name.Contains("同时删除"))?.AsCheckBox();
                if (check is not null) break;
                Thread.Sleep(100);
            }
            if (check is null || check.IsChecked == true) throw new Exception("Data deletion must be an explicit unchecked option");
            check.IsChecked = true; window.FindFirstDescendant(cf => cf.ByAutomationId("1"))!.AsButton().Invoke();
            AutomationElement? yes = null; deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                yes = automation.GetDesktop().FindAllDescendants(cf => cf.ByProcessId(app.ProcessId).And(cf.ByAutomationId("6"))).FirstOrDefault();
                if (yes is not null) break;
                Thread.Sleep(100);
            }
            if (yes is null) throw new Exception("No second deletion confirmation");
            yes.AsButton().Invoke(); deadline = DateTime.UtcNow.AddSeconds(30);
            while (Directory.Exists(defaultWorkspace) && DateTime.UtcNow < deadline) Thread.Sleep(100);
            if (Directory.Exists(defaultWorkspace)) throw new Exception("Explicit data removal did not finish");
            Console.WriteLine("PASS NSIS explicit data removal requires unchecked opt-in and second confirmation");
            window.Close();
        }
        finally { if (!app.HasExited) app.Kill(); }
    }
}
