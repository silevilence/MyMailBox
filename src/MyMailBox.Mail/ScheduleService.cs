using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ScheduleService : IScheduleService
{
    private readonly SqliteMailStore _store;
    private readonly ISchedulePlanner _planner;
    private readonly INotificationSink _notifications;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1);
    public ScheduleService(SqliteMailStore store, ISchedulePlanner planner, INotificationSink notifications, TimeProvider? clock = null)
    {
        _store = store; _planner = planner; _notifications = notifications; _clock = clock ?? TimeProvider.System;
        using var db = store.Open(); using var command = db.CreateCommand(); command.CommandText = """
            CREATE TABLE IF NOT EXISTS schedules(id TEXT PRIMARY KEY,title TEXT NOT NULL,rule TEXT NOT NULL,mode INTEGER NOT NULL,next_utc TEXT,last_completed_utc TEXT,enabled INTEGER NOT NULL,error TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS schedule_runs(id TEXT PRIMARY KEY,plan_id TEXT NOT NULL,due_utc TEXT NOT NULL,state TEXT NOT NULL,completed_utc TEXT,error TEXT NOT NULL DEFAULT '',payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS schedules_due ON schedules(enabled,next_utc);
            """; command.ExecuteNonQuery();
    }
    public IReadOnlyList<CalendarChoice> Calendars => _planner.Calendars;
    public Task<IReadOnlyList<ScheduleOccurrence>> Preview(RecurrenceRule rule, CancellationToken cancellation = default) => Task.Run(() =>
    { cancellation.ThrowIfCancellationRequested(); return _planner.Preview(rule, _clock.GetUtcNow().AddTicks(-1)); }, cancellation);
    public async Task<ScheduleInfo> Create(ScheduleDraft draft, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200) throw new InvalidDataException("计划内容需为 1–200 字。");
        var occurrences = await Preview(draft.Rule, cancellation); if (occurrences.Count == 0) throw new InvalidDataException("所选范围没有未来执行时刻，请调整结束时间或排除条件。");
        var id = Guid.NewGuid();
        await Task.Run(() =>
        {
            using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT INTO schedules(id,title,rule,mode,next_utc,enabled) VALUES($id,$title,$rule,$mode,$next,1)";
            command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$title", draft.Title.Trim()); command.Parameters.AddWithValue("$rule", JsonSerializer.Serialize(draft.Rule)); command.Parameters.AddWithValue("$mode", (int)draft.Notification); command.Parameters.AddWithValue("$next", occurrences[0].Utc.ToUniversalTime().ToString("O"));
            cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
        }, cancellation);
        return (await List(cancellation)).Single(p => p.Id == id);
    }
    public Task<IReadOnlyList<ScheduleInfo>> List(CancellationToken cancellation = default) => Task.Run<IReadOnlyList<ScheduleInfo>>(() =>
    {
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT id,title,rule,next_utc,last_completed_utc,enabled,error FROM schedules ORDER BY enabled DESC,next_utc,id";
        using var reader = command.ExecuteReader(); var result = new List<ScheduleInfo>();
        while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), JsonSerializer.Deserialize<RecurrenceRule>(reader.GetString(2))!, reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)), reader.GetBoolean(5), reader.GetString(6))); }
        return result;
    }, cancellation);
    public async Task SetEnabled(Guid id, bool enabled, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var plan = (await List(cancellation)).SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("计划不存在。");
            var next = enabled ? _planner.Preview(plan.Rule, _clock.GetUtcNow().AddTicks(-1), 1, plan.LastCompletedUtc).FirstOrDefault()?.Utc : plan.NextUtc;
            if (enabled && next is null) throw new InvalidDataException("此计划已无未来执行时刻。");
            await Task.Run(() =>
            {
                using var db = _store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE schedules SET enabled=$enabled,next_utc=$next,error='' WHERE id=$id; UPDATE schedule_runs SET state='canceled' WHERE plan_id=$id AND state='pending';";
                command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0); command.Parameters.AddWithValue("$next", next?.ToUniversalTime().ToString("O") is { } value ? value : DBNull.Value);
                cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); transaction.Commit();
            }, cancellation);
        }
        finally { _gate.Release(); }
    }
    public async Task<int> Tick(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var runs = await Task.Run(() =>
            {
                using var db = _store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT id,title,next_utc,mode FROM schedules WHERE enabled=1 AND next_utc<=$now ORDER BY next_utc LIMIT 100";
                command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUniversalTime().ToString("O")); var due = new List<ReminderNotice>();
                using (var reader = command.ExecuteReader()) while (reader.Read())
                {
                    var id = Guid.Parse(reader.GetString(0)); var instant = DateTimeOffset.Parse(reader.GetString(2));
                    due.Add(new("schedule:" + id.ToString("N") + ":" + instant.UtcTicks, id, reader.GetString(1), instant, null, (ReminderNotificationMode)reader.GetInt32(3)));
                }
                foreach (var notice in due)
                {
                    cancellation.ThrowIfCancellationRequested(); command.Parameters.Clear(); command.CommandText = "INSERT OR IGNORE INTO schedule_runs(id,plan_id,due_utc,state,payload) VALUES($id,$plan,$due,'pending',$payload)";
                    command.Parameters.AddWithValue("$id", notice.DeliveryId); command.Parameters.AddWithValue("$plan", notice.ReminderId.ToString()); command.Parameters.AddWithValue("$due", notice.DueUtc.ToString("O")); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(notice)); command.ExecuteNonQuery();
                }
                command.Parameters.Clear(); command.CommandText = "SELECT payload FROM schedule_runs r JOIN schedules s ON r.plan_id=s.id WHERE r.state='pending' AND s.enabled=1 ORDER BY r.due_utc LIMIT 100";
                var result = new List<ReminderNotice>(); using (var reader = command.ExecuteReader()) while (reader.Read()) result.Add(JsonSerializer.Deserialize<ReminderNotice>(reader.GetString(0))!);
                cancellation.ThrowIfCancellationRequested(); transaction.Commit(); return result;
            }, cancellation);
            var delivered = 0;
            foreach (var notice in runs)
            {
                cancellation.ThrowIfCancellationRequested(); string error = ""; DateTimeOffset? next = null; DateTimeOffset? completed = null;
                try
                {
                    await _notifications.Publish(notice, cancellation); completed = _clock.GetUtcNow();
                    var plan = (await List(cancellation)).Single(p => p.Id == notice.ReminderId);
                    next = _planner.Preview(plan.Rule, completed.Value, 1, completed).FirstOrDefault()?.Utc; delivered++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { error = e.Message; }
                await Task.Run(() =>
                {
                    using var db = _store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = error.Length == 0
                        ? "UPDATE schedule_runs SET state='completed',completed_utc=$completed,error='' WHERE id=$run; UPDATE schedules SET next_utc=$next,last_completed_utc=$completed,enabled=CASE WHEN $next IS NULL THEN 0 ELSE 1 END,error='' WHERE id=$plan"
                        : "UPDATE schedule_runs SET error=$error WHERE id=$run; UPDATE schedules SET error=$error WHERE id=$plan";
                    command.Parameters.AddWithValue("$run", notice.DeliveryId); command.Parameters.AddWithValue("$plan", notice.ReminderId.ToString());
                    if (error.Length == 0) { command.Parameters.AddWithValue("$completed", completed!.Value.ToUniversalTime().ToString("O")); command.Parameters.AddWithValue("$next", next?.ToUniversalTime().ToString("O") is { } value ? value : DBNull.Value); }
                    else command.Parameters.AddWithValue("$error", error);
                    command.ExecuteNonQuery(); transaction.Commit();
                }, cancellation);
            }
            return delivered;
        }
        finally { _gate.Release(); }
    }
}
