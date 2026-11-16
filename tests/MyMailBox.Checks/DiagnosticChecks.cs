using MyMailBox.Runtime;

internal static class DiagnosticChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(PluginChecks.Repository, "artifacts/checks/diagnostics-" + Guid.NewGuid().ToString("N"));
        var log = new Diagnostics(folder, maximumBytes: 1024, retainedFiles: 3);
        var operations = new HostOperations(); var performed = 0;
        operations.Register("mail.read", (_, _) => { performed++; return Task.FromResult("test body SECRET-BODY"); });
        var policy = new PermissionPolicy(["mail.read", "mail.send"], ["mail.read:work"]);
        using var scope = new PluginScope("test", new Contributions(), new EventHub(), policy, operations, log);
        await PluginChecks.Reject(() => scope.Invoke("mail.send", "work"), "未授予");
        await PluginChecks.Reject(() => scope.Invoke("mail.read", "personal"), "未授予");
        foreach (var capability in new[] { "accounts.read", "mail.delete", "files.read", "files.write", "network.request" })
            await PluginChecks.Reject(() => scope.Invoke(capability), "未授予");
        PluginChecks.Check(performed == 0, "Unauthorized host operation ran");
        await scope.Invoke("mail.read", "work"); PluginChecks.Check(performed == 1, "Scoped authorized operation did not run");
        log.Record("password=SECRET-PASSWORD", "token=SECRET-TOKEN", "failed", "sample-error");
        var exported = log.Export(Path.Combine(folder, "export.json"));
        var text = File.ReadAllText(exported);
        PluginChecks.Check(!text.Contains("SECRET-") && text.Contains("correlationId") && text.Contains("permission-denied"), "Diagnostic export leaked data or lost correlation");
        for (var i = 0; i < 100; i++) log.Record("test", "sample", "success");
        var files = Directory.GetFiles(folder, "diagnostics.jsonl*");
        PluginChecks.Check(files.Length <= 3 && files.All(p => new FileInfo(p).Length < 1600), "Diagnostic rotation unbounded");
        var complete = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.Register("network.request", (_, _) => complete.Task);
        using var networkScope = new PluginScope("network", new Contributions(), new EventHub(), new(["network.request"], ["network.request"]), operations, log);
        var pending = networkScope.Invoke("network.request");
        var draining = networkScope.Drain(false, TimeSpan.FromSeconds(2));
        await PluginChecks.Reject(() => networkScope.Invoke("network.request"), "停止接单");
        PluginChecks.Check(!draining.IsCompleted, "Direct host operation bypassed drain");
        complete.SetResult("done"); await pending; await draining;
        Console.WriteLine("PASS diagnostics: deny before host action, account-scoped grant, allowlisted export, correlation IDs, bounded log rotation");
    }
}
