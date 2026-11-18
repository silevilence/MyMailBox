using System.Diagnostics;
using System.Security.Cryptography;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static partial class TransferChecks
{
    private sealed class AtPhase(Action<MailTransfer> action) : IProgress<MailTransfer>
    {
        public void Report(MailTransfer value) => action(value);
    }

    public static async Task CrashWorker(string directory, Guid op, string phase)
    {
        var prefix = Path.GetFullPath(Path.Combine(PluginChecks.Repository, "artifacts/checks/recovery-"));
        if (!Path.GetFullPath(directory).StartsWith(prefix,StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory)["recovery-".Length..],"N",out _))
            throw new InvalidDataException("Crash worker requires an isolated recovery fixture");
        var service = new ProtocolMailService(new SqliteMailStore(directory));
        await service.Continue(op,new AtPhase(step =>
        {
            if (step.Phase == phase) Environment.Exit(77);
        }));
        throw new Exception("Requested crash boundary was not reached");
    }

    public static async Task Recovery()
    {
        var directory = Path.Combine(PluginChecks.Repository, "artifacts/checks/recovery-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(directory);
        var accounts = new AccountService(store);
        var service = new ProtocolMailService(store);
        IMailTransferService transfer = service;
        var account = await accounts.Save(new(null,"恢复测试","local"));
        var source = (await store.Folders()).Single(f=>f.AccountId==account.Id);
        var raw = MailChecks.Sample();
        var parsed = await new MyMailBox.Mail.MimeReader().Parse(raw);
        async Task<TransferRequest> Plan()
        {
            var key = await store.Import(source.Id,raw,parsed);
            var req = new TransferRequest(Guid.NewGuid(),account.Id,source.Id,key.ToString(),SqliteMailStore.Inbox,true);
            await service.Plan(req); return req;
        }
        int expectedTargets = 0;
        foreach (var phase in new[]{"planned","stored","verified","cleaning","done"})
        {
            var req = await Plan();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false,CreateNoWindow = true };
            foreach(var arg in new[]{typeof(TransferChecks).Assembly.Location,"transfer-crash-worker",directory,req.Id.ToString(),phase})
                start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            MailChecks.Check(child.ExitCode==77,"Fixture process did not exit at "+phase);
            var reopened = new ProtocolMailService(new SqliteMailStore(directory));
            var result = await reopened.Continue(req.Id);
            var replay = await reopened.Continue(req.Id);
            MailChecks.Check(result.Phase=="done" && result.TargetMessage==replay.TargetMessage &&
                (await store.Read(result.TargetMessage!.Value)).SequenceEqual(raw),"Crash recovery lost target at "+phase);
            MailChecks.Check((await store.List(SqliteMailStore.Inbox,0,100)).Items.Count == ++expectedTargets,"Duplicate after crash at "+phase);
        }

        var changed = await Plan();
        using (var stop = new CancellationTokenSource())
        {
            try
            {
                await service.Continue(changed.Id,new AtPhase(s=>{ if(s.Phase=="verified") stop.Cancel(); }),stop.Token);
                throw new Exception("Expected cancellation");
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        }
        using(var db=store.Open())
        using(var c=db.CreateCommand())
        {
            var replacement=MailChecks.Sample().Concat("changed"u8.ToArray()).ToArray();
            c.CommandText="UPDATE messages SET raw=$raw,sha256=$hash,size=$size WHERE id=$key";
            c.Parameters.AddWithValue("$raw",replacement); c.Parameters.AddWithValue("$hash",Convert.ToHexString(SHA256.HashData(replacement)));
            c.Parameters.AddWithValue("$size",replacement.Length); c.Parameters.AddWithValue("$key",changed.Key); c.ExecuteNonQuery();
        }
        var conflict = await service.Continue(changed.Id);
        MailChecks.Check(conflict.Phase=="cleaning" && conflict.Detail.Contains("变化") &&
            (await store.Read(Guid.Parse(changed.Key))).Length>raw.Length,"Changed source was blindly removed");
        await transfer.Cancel(changed.Id);
        MailChecks.Check((await service.Continue(changed.Id)).Phase=="canceled" && (await store.Read(conflict.TargetMessage!.Value)).SequenceEqual(raw),
            "Cancel after verification damaged target");
        var events=await transfer.History(changed.Id);
        MailChecks.Check(events.Any(e=>e.Phase=="stored") && events.Any(e=>e.Detail.Contains("变化")) && events[^1].Phase=="canceled","Transfer history lacks failure stages");

        var interrupted=await Plan();
        var pages=new Contributions();
        using var scope=new PluginScope("transfer-test",pages,new EventHub(),new([],[]),new(),null);
        scope.Add(new Page("transfer","transfer","",[],[],(_,_)=>Task.FromResult("")) {Organization=new(accounts,service){Transfers=service}});
        var scoped=pages.Pages.Single().Organization!.Transfers!;
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        var work=Task.Run(()=>scoped.Continue(interrupted.Id,new AtPhase(s=>
        {
            if(s.Phase=="stored") { reached.TrySetResult(); if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
        })));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var drain=scope.Drain(true,TimeSpan.FromSeconds(10));
        release.Set();
        try { await work; throw new Exception("Plugin stop did not cancel transfer"); }
        catch(OperationCanceledException) { }
        await drain;
        var done=await service.Continue(interrupted.Id);
        MailChecks.Check(done.Phase=="done" && scope.ActiveOperations==0,"Plugin stop/resume failed");
        Console.WriteLine("PASS transfer recovery: real process exits at five durable stages, cancellation, changed source, retained verified copy, operation history and plugin drain/restart");
    }
}
