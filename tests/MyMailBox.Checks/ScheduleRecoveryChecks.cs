using System.Collections.Concurrent;
using System.Diagnostics;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class ScheduleRecoveryChecks
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static RecurrenceRule Rule => new() { Kind = RecurrenceKind.FixedInterval, Interval = TimeSpan.FromMinutes(1), StartLocal = Start.UtcDateTime, TimeZoneId = "UTC" };
    public static async Task Run()
    {
        foreach (var missed in Enum.GetValues<MissedRunPolicy>())
        {
            var f = Fixture(); var service = f.Service(f.Sink); var plan = await service.Create(new("missed", Rule) { Execution = new() { Missed = missed, CatchUpLimit = 3 } });
            f.Clock.Advance(TimeSpan.FromMinutes(10)); for (var i = 0; i < 4; i++) await service.Tick();
            var expected = missed == MissedRunPolicy.Skip ? 0 : missed == MissedRunPolicy.Once ? 1 : 3;
            Check(f.Notices.Count == expected, "Missed policy count wrong: " + missed);
            Check((await service.List()).Single().NextUtc == Start.AddMinutes(11), "Catch-up did not bound backlog");
        }
        {
            var f = Fixture(); var service = f.Service(f.Sink); await service.Create(new("old", Rule) { Execution = new() { Missed = MissedRunPolicy.LimitedCatchUp, CatchUpLimit = 3, MaximumAge = TimeSpan.FromMinutes(2) } });
            f.Clock.Advance(TimeSpan.FromMinutes(10)); for (var i = 0; i < 3; i++) await service.Tick();
            Check(f.Notices.Count == 3 && f.Notices.All(n => n.DueUtc >= Start.AddMinutes(8)), "Expired catch-up executed outside age window");
        }
        foreach (var overlap in new[] { ScheduleOverlapPolicy.Skip, ScheduleOverlapPolicy.Queue, ScheduleOverlapPolicy.Parallel }) await Overlap(overlap);
        await Mutex(); await RetryAndTimeout(); await CrashRecovery();
        {
            var f = Fixture(); var sink = new BlockingSink(); var service = f.Service(sink); var plan = await service.Create(new("stop runner", Rule));
            var runner = new ScheduleRunner(service); await sink.WaitFor(1); runner.Dispose();
            Check((await service.History(plan.Id)).Single().State == "retry" && f.Notices.Count == 0, "GUI/plugin stop pretended unfinished action completed");
            f.Clock.Advance(TimeSpan.FromSeconds(30)); await f.Service(f.Sink).Tick(); Check(f.Notices.Count == 1, "Graceful stop failed to resume persisted work");
        }
        {
            var f = Fixture(); var service = f.Service(f.Sink); var finite = await service.Create(new("last occurrence", Rule with { EndLocal = Start.UtcDateTime }));
            await service.Tick(); Check(f.Notices.Count == 1 && !(await service.List()).Single().Enabled, "Final occurrence disabled before delivery");
            var next = await service.Create(new("clock", Rule)); await service.Tick(); var count = f.Notices.Count;
            f.Clock.Advance(TimeSpan.FromHours(-1)); await service.Tick(); Check(f.Notices.Count == count, "Clock backwards repeated an execution");
            f.Clock.Now = Start.AddMinutes(1); await service.Tick(); Check(f.Notices.Count == count + 1, "Clock recovery did not continue");
            var history = await service.History(finite.Id); Check(history.Single().State == "completed", "Finished history lost");
            try { await service.CancelRun(history[0].Id); throw new Exception("Completed run cancellation changed plan"); } catch (InvalidOperationException) { }
        }
        Console.WriteLine("PASS scheduler recovery: skip/once/bounded age catch-up, skip/queue/parallel/mutex, persisted exponential retry/timeout/cancel, real process-kill lease recovery, duplicate claim prevention, backward clock and finite end");
    }
    private static async Task Overlap(ScheduleOverlapPolicy policy)
    {
        var f = Fixture(); var sink = new BlockingSink(); var service = f.Service(sink);
        var plan = await service.Create(new("overlap", Rule) { Execution = new() { Overlap = policy, MaxConcurrency = 2, Timeout = TimeSpan.FromMinutes(5) } });
        var first = service.Tick(); await sink.WaitFor(1);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); var second = service.Tick();
        if (policy == ScheduleOverlapPolicy.Parallel) await sink.WaitFor(2); else await second;
        Check(sink.Count == (policy == ScheduleOverlapPolicy.Parallel ? 2 : 1), "Overlap policy violated");
        var history = await service.History(plan.Id);
        Check(history.Count == 2 && history.Count(r => r.State == "running") == (policy == ScheduleOverlapPolicy.Parallel ? 2 : 1), "Overlap state not persisted");
        if (policy == ScheduleOverlapPolicy.Queue) Check(history.Any(r => r.State == "pending"), "Queue did not persist pending work");
        if (policy == ScheduleOverlapPolicy.Skip) Check(history.Any(r => r.State == "skipped"), "Skip not recorded");
        if (policy == ScheduleOverlapPolicy.Parallel)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1)); await service.Tick(); Check(sink.Count == 2, "Parallel cap exceeded");
        }
        sink.Release(); await Task.WhenAll(first, second); await service.Tick();
        Check(sink.Count == (policy == ScheduleOverlapPolicy.Skip ? 1 : policy == ScheduleOverlapPolicy.Queue ? 2 : 3), "Queued work did not drain");
    }
    private static async Task Mutex()
    {
        var f = Fixture(); var sink = new BlockingSink(); var service = f.Service(sink);
        var policy = new ScheduleExecutionPolicy { Overlap = ScheduleOverlapPolicy.Mutex, MutexKey = "shared", Timeout = TimeSpan.FromMinutes(5) };
        var a = await service.Create(new("mutex A", Rule) { Execution = policy }); await service.Create(new("mutex B", Rule) { Execution = policy });
        var first = service.Tick(); await sink.WaitFor(1); await f.Service(sink).Tick(); Check(sink.Count == 1, "Independent service bypassed shared mutex/lease");
        sink.Release(); await first; await service.Tick(); Check(sink.Count == 2, "Mutex queue did not drain");
        var cancelFixture = Fixture(); var blocked = new BlockingSink(); var cancelService = cancelFixture.Service(blocked);
        var plan = await cancelService.Create(new("cancel", Rule)); var running = cancelService.Tick(); await blocked.WaitFor(1);
        var run = (await cancelService.History(plan.Id)).Single(); await cancelService.CancelRun(run.Id); await running;
        Check((await cancelService.History(plan.Id)).Single().State == "canceled", "Cancellation not persisted");
        await cancelFixture.Service(cancelFixture.Sink).Tick(); Check(cancelFixture.Notices.Count == 0, "Canceled occurrence was retried");
    }
    private static async Task RetryAndTimeout()
    {
        var f = Fixture(); var attempts = 0;
        var flaky = new DelegateSink(async (notice, token) => { if (Interlocked.Increment(ref attempts) < 3) throw new HttpRequestException("模拟断网"); await f.Sink.Publish(notice, token); });
        var service = f.Service(flaky); var plan = await service.Create(new("retry", Rule) { Execution = new() { RetryCount = 2, RetryDelay = TimeSpan.FromSeconds(1) } });
        await service.Tick(); var first = (await service.History(plan.Id)).Single(); Check(first is { State: "retry", Attempts: 1 } && first.RetryUtc == Start.AddSeconds(1), "First retry not persisted");
        await f.Service(flaky).Tick(); Check(attempts == 1, "Restart retried before backoff");
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Service(flaky).Tick(); var second = (await service.History(plan.Id)).Single(); Check(second.Attempts == 2 && second.RetryUtc == Start.AddSeconds(3), "Exponential retry incorrect");
        f.Clock.Advance(TimeSpan.FromSeconds(2)); await f.Service(flaky).Tick(); Check(f.Notices.Count == 1 && (await service.History(plan.Id)).Single() is { State: "completed", Attempts: 3 }, "Network recovery failed");
        var exhausted = Fixture(); var failing = exhausted.Service(new DelegateSink((_, _) => throw new IOException("模拟持续失败")));
        var failedPlan = await failing.Create(new("exhaust", Rule) { Execution = new() { RetryCount = 1, RetryDelay = TimeSpan.FromSeconds(1) } }); await failing.Tick(); exhausted.Clock.Advance(TimeSpan.FromSeconds(1)); await failing.Tick();
        Check((await failing.History(failedPlan.Id)).Single() is { State: "failed", Attempts: 2 }, "Retries did not stop at bound");
        var timeoutFixture = Fixture(); var timeout = timeoutFixture.Service(new BlockingSink());
        var timed = await timeout.Create(new("timeout", Rule) { Execution = new() { Timeout = TimeSpan.FromMilliseconds(30), RetryCount = 0 } }); await timeout.Tick();
        Check((await timeout.History(timed.Id)).Single() is { State: "timedout", Attempts: 1 }, "Timeout not recorded");
    }
    private static async Task CrashRecovery()
    {
        var f = Fixture(); var service = f.Service(f.Sink); var plan = await service.Create(new("crash", Rule) { Execution = new() { Timeout = TimeSpan.FromSeconds(30), RetryDelay = TimeSpan.FromSeconds(1) } });
        var ready = Path.Combine(f.Root, "claimed"); var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(ScheduleRecoveryChecks).Assembly.Location); start.ArgumentList.Add("schedule-crash-worker"); start.ArgumentList.Add(f.Root); start.ArgumentList.Add(ready);
        using var child = Process.Start(start)!;
        try
        {
            for (var i = 0; !File.Exists(ready) && i < 200; i++) { if (child.HasExited) throw new Exception("Schedule crash worker exited early"); await Task.Delay(50); }
            Check(File.Exists(ready), "Schedule crash worker did not claim"); child.Kill(); await child.WaitForExitAsync();
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        var restored = f.Service(f.Sink); await restored.Tick(); Check(f.Notices.Count == 0 && (await restored.History(plan.Id)).Single().State == "running", "Startup duplicated unexpired claim");
        f.Clock.Advance(TimeSpan.FromSeconds(36)); await restored.Tick(); Check(f.Notices.Count == 0, "Recovered lease skipped backoff");
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await restored.Tick();
        Check(f.Notices.Count == 1 && (await restored.History(plan.Id)).Single() is { State: "completed", Attempts: 2 }, "Crash recovery lost occurrence or attempts");
        await restored.Tick(); Check(f.Notices.Count == 1, "Recovered occurrence delivered twice");
    }
    public static async Task CrashWorker(string root, string ready)
    {
        var service = new ScheduleService(new SqliteMailStore(root), new RecurrencePlanner(), new DelegateSink(async (_, token) => { await File.WriteAllTextAsync(ready, "claimed", token); await Task.Delay(Timeout.InfiniteTimeSpan, token); }), new Clock());
        await service.Tick();
    }
    private static void Check(bool value, string message) => MailChecks.Check(value, message);
    private static TestFixture Fixture() => new();
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = Start; public override DateTimeOffset GetUtcNow() => Now; public void Advance(TimeSpan span) => Now += span;
    }
    private sealed class TestFixture
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MyMailBox-schedule-recovery-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new(); public ConcurrentBag<ReminderNotice> Notices { get; } = [];
        public SqliteMailStore Store { get; } public INotificationSink Sink { get; }
        public TestFixture() { Store = new(Root); Sink = new SqliteNotificationSink(Store, Notices.Add); }
        public ScheduleService Service(INotificationSink sink) => new(Store, new RecurrencePlanner(), sink, Clock);
    }
    private sealed class DelegateSink(Func<ReminderNotice, CancellationToken, Task> publish) : INotificationSink
    { public Task Publish(ReminderNotice notice, CancellationToken cancellation = default) => publish(notice, cancellation); public Task Dismiss(string id, CancellationToken cancellation = default) => Task.CompletedTask; }
    private sealed class BlockingSink : INotificationSink
    {
        private int _count; public int Count => Volatile.Read(ref _count);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Publish(ReminderNotice notice, CancellationToken cancellation = default) { Interlocked.Increment(ref _count); await _release.Task.WaitAsync(cancellation); }
        public Task Dismiss(string id, CancellationToken cancellation = default) => Task.CompletedTask;
        public void Release() => _release.TrySetResult();
        public async Task WaitFor(int count) { for (var i = 0; Count < count && i < 200; i++) await Task.Delay(10); Check(Count >= count, "Execution did not start"); }
    }
}
