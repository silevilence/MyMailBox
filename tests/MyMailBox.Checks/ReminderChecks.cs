using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class ReminderChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "MyMailBox-reminders-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(root); var accounts = new AccountService(store);
        var raw = MimeChecks.Multipart(); var message = await store.Import(SqliteMailStore.Inbox, raw, await new MimeReader().Parse(raw));
        var clock = new Clock(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero)); var shown = new List<ReminderNotice>();
        var sink = new SqliteNotificationSink(store, shown.Add); IReminderService service = new ReminderService(store, sink, clock);
        var first = await service.Create(new("处理中文邮件", new DateTime(2026, 11, 17, 8, 1, 0), "China Standard Time", message));
        MailChecks.Check(first.DueUtc == clock.GetUtcNow().AddMinutes(1), "Time zone conversion failed");
        MailChecks.Check(await service.Tick() == 0 && shown.Count == 0, "Reminder fired early");
        service = new ReminderService(new SqliteMailStore(root), sink, clock);
        clock.Advance(TimeSpan.FromMinutes(2)); MailChecks.Check(await service.Tick() == 1 && shown.Count == 1, "Restart missed due reminder");
        await service.Tick(); await new ReminderService(store, new SqliteNotificationSink(store, shown.Add), clock).Tick();
        MailChecks.Check(shown.Count == 1, "Restart repeated same reminder");
        await service.Snooze(first.Id, TimeSpan.FromMinutes(5)); clock.Advance(TimeSpan.FromMinutes(4)); await service.Tick(); MailChecks.Check(shown.Count == 1, "Snooze fired early");
        clock.Advance(TimeSpan.FromMinutes(1)); await service.Tick(); MailChecks.Check(shown.Count == 2 && shown[0].DeliveryId != shown[1].DeliveryId, "Snooze delivery identity missing");
        await service.Dismiss(first.Id); clock.Advance(TimeSpan.FromDays(1)); await service.Tick(); MailChecks.Check(shown.Count == 2 && (await service.List()).Single().State == "dismissed", "Dismiss repeated reminder");
        var canceled = await service.Create(new("取消事项", clock.GetUtcNow().AddMinutes(1).UtcDateTime, "UTC")); await service.Cancel(canceled.Id);
        clock.Advance(TimeSpan.FromMinutes(2)); await service.Tick(); MailChecks.Check(shown.Count == 2, "Canceled reminder fired");
        var silent = await service.Create(new("静默事项", clock.GetUtcNow().UtcDateTime, "UTC", Notification: ReminderNotificationMode.ListOnly)); await service.Tick();
        MailChecks.Check(shown.Count == 2 && (await service.List()).Single(r => r.Id == silent.Id).State == "notified", "Silent mode lost state or displayed banner");
        var target = await accounts.Save(new(null, "已移动邮箱", "local")); var folder = (await store.Folders()).Single(f => f.AccountId == target.Id);
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "UPDATE messages SET folder_id=$folder WHERE id=$id"; command.Parameters.AddWithValue("$folder", folder.Id.ToString()); command.Parameters.AddWithValue("$id", message.ToString()); command.ExecuteNonQuery();
        }
        MailChecks.Check((await service.List()).Single(r => r.Id == first.Id).MessageState.Contains("已移动邮箱"), "Moved message reference stale");
        await accounts.Remove(target.Id); MailChecks.Check((await service.List()).Single(r => r.Id == first.Id).MessageState.Contains("原文保留"), "Removed account reference unclear");
        using (var db = store.Open()) { using var command = db.CreateCommand(); command.CommandText = "DELETE FROM messages WHERE id=$id"; command.Parameters.AddWithValue("$id", message.ToString()); command.ExecuteNonQuery(); }
        MailChecks.Check((await service.List()).Single(r => r.Id == first.Id).MessageState.Contains("已删除"), "Deleted message reference unclear");
        var flaky = new AfterDeliveryFailure(sink); var retry = new ReminderService(store, flaky, clock);
        var uncertain = await retry.Create(new("投递确认中断", clock.GetUtcNow().UtcDateTime, "UTC")); await retry.Tick();
        MailChecks.Check((await retry.List()).Single(r => r.Id == uncertain.Id).Error.Contains("模拟"), "Notification failure not visible");
        await new ReminderService(store, new SqliteNotificationSink(store, shown.Add), clock).Tick();
        MailChecks.Check(shown.Count(n => n.ReminderId == uncertain.Id) == 1 && (await retry.List()).Single(r => r.Id == uncertain.Id).Error == "", "Outbox replay duplicated banner or failed recovery");
        await Reject(() => service.Create(new("无效时间", new(2026, 3, 8, 2, 30, 0), "Eastern Standard Time")), "夏令时");
        await Reject(() => service.Create(new("重复时间", new(2026, 11, 1, 1, 30, 0), "Eastern Standard Time")), "重复");
        var recording = new RecordingScheduler(); var runner = new ReminderRunner(recording); await recording.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); runner.Dispose();
        var count = recording.Count; await Task.Delay(1100); MailChecks.Check(recording.Count == count, "Stopped reminder runner kept executing");
        Console.WriteLine("PASS reminders: virtual clock/time zone, persisted restart/overdue once, snooze/dismiss/cancel/silent, moved/deleted/closed message refs, idempotent outbox retry, DST rejection, timer lifecycle and replaceable contracts");
    }
    private static async Task Reject(Func<Task> action, string fragment)
    { try { await action(); } catch (InvalidDataException e) when (e.Message.Contains(fragment)) { return; } throw new Exception("Expected " + fragment); }
    private sealed class Clock(DateTimeOffset current) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => current; public void Advance(TimeSpan value) => current += value; }
    private sealed class AfterDeliveryFailure(INotificationSink inner) : INotificationSink
    {
        public async Task Publish(ReminderNotice notice, CancellationToken cancellation = default) { await inner.Publish(notice, cancellation); throw new IOException("模拟投递后确认中断"); }
        public Task Dismiss(string id, CancellationToken cancellation = default) => inner.Dismiss(id, cancellation);
    }
    private sealed class RecordingScheduler : IReminderService
    {
        public int Count; public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Tick(CancellationToken cancellation = default) { Interlocked.Increment(ref Count); Started.TrySetResult(); return Task.FromResult(0); }
        public Task<ReminderInfo> Create(ReminderDraft draft, CancellationToken cancellation = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReminderInfo>> List(CancellationToken cancellation = default) => Task.FromResult<IReadOnlyList<ReminderInfo>>([]);
        public Task Snooze(Guid id, TimeSpan delay, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task Dismiss(Guid id, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task Cancel(Guid id, CancellationToken cancellation = default) => Task.CompletedTask;
    }
}
