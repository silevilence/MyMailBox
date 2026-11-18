using MyMailBox.Runtime;
using System.Diagnostics;

internal static class WorkspaceOwnerChecks
{
    internal static void TryOpen(string path)
    {
        using var owner=WorkspaceOwner.Open(path);
        Environment.ExitCode=owner is null?0:2;
    }
    internal static async Task Run()
    {
        string directory=Path.Combine(PluginChecks.Repository,"artifacts/checks/owner-"+Guid.NewGuid().ToString("N"));
        using(var first=WorkspaceOwner.Open(directory)??throw new Exception("未获取工作空间"))
        {
            var restored=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);first.OnRestore(()=>restored.TrySetResult());
            var info=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add(typeof(WorkspaceOwnerChecks).Assembly.Location);
            info.ArgumentList.Add("owner-probe");info.ArgumentList.Add(directory);
            using var child=Process.Start(info)!;await child.WaitForExitAsync();await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            MailChecks.Check(child.ExitCode==0,"Second process acquired same workspace");
            using var isolated=WorkspaceOwner.Open(Path.Combine(directory,"independent"));
            MailChecks.Check(isolated is not null,"Independent restored workspace blocked");
        }
        using var replacement=WorkspaceOwner.Open(directory);MailChecks.Check(replacement is not null,"Workspace ownership survived exit");
        Console.WriteLine("PASS one owner per workspace, independent process activates owner through current-user pipe, new owner after exit");
    }
}
