using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ScheduleService : IScheduleService
{
    private readonly SqliteMailStore _store;
    private readonly ISchedulePlanner _planner;
    private readonly INotificationSink _notifications;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, (Guid Plan, CancellationTokenSource Cancel)> _active = new();
    private sealed record StoredPlan(ScheduleInfo Info, ReminderNotificationMode Mode);
    private sealed record Claim(ReminderNotice Notice, ScheduleExecutionPolicy Policy, int Attempt);

    public ScheduleService(SqliteMailStore store, ISchedulePlanner planner, INotificationSink notifications, TimeProvider? clock = null)
    {
        _store = store; _planner = planner; _notifications = notifications; _clock = clock ?? TimeProvider.System;
        using var db = store.Open(); using var transaction = db.BeginTransaction();
        Execute(db, transaction, """
            CREATE TABLE IF NOT EXISTS schedules(id TEXT PRIMARY KEY,title TEXT NOT NULL,rule TEXT NOT NULL,mode INTEGER NOT NULL,next_utc TEXT,last_completed_utc TEXT,enabled INTEGER NOT NULL,error TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS schedule_runs(id TEXT PRIMARY KEY,plan_id TEXT NOT NULL,due_utc TEXT NOT NULL,state TEXT NOT NULL,completed_utc TEXT,error TEXT NOT NULL DEFAULT '',payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS schedules_due ON schedules(enabled,next_utc);
            """);
        AddColumn("schedules", "policy", "TEXT NOT NULL DEFAULT '{}'");
        AddColumn("schedule_runs", "attempts", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("schedule_runs", "retry_utc", "TEXT"); AddColumn("schedule_runs", "lease_owner", "TEXT"); AddColumn("schedule_runs", "lease_until_utc", "TEXT");
        transaction.Commit();
        void AddColumn(string table, string column, string definition)
        {
            using var command = Command(db, transaction, "PRAGMA table_info(" + table + ")");
            var columns = new HashSet<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
            if (!columns.Contains(column)) Execute(db, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }
    }
    public IReadOnlyList<CalendarChoice> Calendars => _planner.Calendars;
    public Task<IReadOnlyList<ScheduleOccurrence>> Preview(RecurrenceRule rule, CancellationToken cancellation = default) => Task.Run(() =>
    { cancellation.ThrowIfCancellationRequested(); return _planner.Preview(rule, _clock.GetUtcNow().AddTicks(-1)); }, cancellation);
    public async Task<ScheduleInfo> Create(ScheduleDraft draft, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200) throw new InvalidDataException("计划内容需为 1–200 字。");
        Validate(draft.Execution);
        var occurrences = await Preview(draft.Rule, cancellation); if (occurrences.Count == 0) throw new InvalidDataException("所选范围没有未来执行时刻。");
        var id = Guid.NewGuid();
        await Task.Run(() =>
        {
            using var db = _store.Open(); cancellation.ThrowIfCancellationRequested();
            Execute(db, null, "INSERT INTO schedules(id,title,rule,mode,next_utc,enabled,policy) VALUES($id,$title,$rule,$mode,$next,1,$policy)",
                ("$id", id.ToString()), ("$title", draft.Title.Trim()), ("$rule", JsonSerializer.Serialize(draft.Rule)), ("$mode", (int)draft.Notification), ("$next", Stamp(occurrences[0].Utc)), ("$policy", JsonSerializer.Serialize(draft.Execution)));
        }, cancellation);
        return (await List(cancellation)).Single(p => p.Id == id);
    }
    public Task<IReadOnlyList<ScheduleInfo>> List(CancellationToken cancellation = default) => Task.Run<IReadOnlyList<ScheduleInfo>>(() =>
    { cancellation.ThrowIfCancellationRequested(); using var db = _store.Open(); return Plans(db, null).Select(p => p.Info).ToArray(); }, cancellation);
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
                using var db = _store.Open(); using var tx = db.BeginTransaction(); cancellation.ThrowIfCancellationRequested();
                Execute(db, tx, "UPDATE schedules SET enabled=$enabled,next_utc=$next,error='' WHERE id=$id; UPDATE schedule_runs SET state=CASE WHEN state='running' THEN 'cancel_requested' ELSE 'canceled' END,error='计划已暂停或重新启用' WHERE plan_id=$id AND state IN ('pending','retry','running');",
                    ("$id", id.ToString()), ("$enabled", enabled ? 1 : 0), ("$next", Stamp(next))); tx.Commit();
            }, cancellation);
            foreach (var active in _active.Values.Where(a => a.Plan == id)) active.Cancel.Cancel();
        }
        finally { _gate.Release(); }
    }
    public Task<IReadOnlyList<ScheduleRunInfo>> History(Guid id, CancellationToken cancellation = default) => Task.Run<IReadOnlyList<ScheduleRunInfo>>(() =>
    {
        using var db = _store.Open(); using var command = Command(db, null, "SELECT id,plan_id,due_utc,state,attempts,retry_utc,completed_utc,error FROM schedule_runs WHERE plan_id=$id ORDER BY due_utc DESC LIMIT 200", ("$id", id.ToString()));
        using var reader = command.ExecuteReader(); var rows = new List<ScheduleRunInfo>();
        while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); rows.Add(new(reader.GetString(0), Guid.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2)), reader.GetString(3), reader.GetInt32(4), Instant(reader, 5), Instant(reader, 6), reader.GetString(7))); }
        return rows;
    }, cancellation);
    public async Task CancelRun(string id, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            await Task.Run(() =>
            {
                using var db = _store.Open(); using var tx = db.BeginTransaction();
                using var find = Command(db, tx, "SELECT plan_id FROM schedule_runs WHERE id=$id", ("$id", id));
                var planId = find.ExecuteScalar() as string ?? throw new InvalidDataException("执行记录不存在。");
                if (Execute(db, tx, "UPDATE schedule_runs SET state=CASE WHEN state='running' THEN 'cancel_requested' ELSE 'canceled' END,completed_utc=$now,error='用户取消' WHERE id=$id AND state IN ('pending','retry','running')", ("$now", Stamp(_clock.GetUtcNow())), ("$id", id)) == 0) throw new InvalidOperationException("该执行已结束或已请求取消。");
                var plan = Plans(db, tx).Single(p => p.Info.Id.ToString() == planId).Info;
                if (plan.Rule.Kind == RecurrenceKind.AfterCompletion) AdvanceAfterCompletion(db, tx, plan, _clock.GetUtcNow());
                cancellation.ThrowIfCancellationRequested(); tx.Commit();
            }, cancellation);
            if (_active.TryGetValue(id, out var active)) active.Cancel.Cancel();
        }
        finally { _gate.Release(); }
    }
    public async Task<int> Tick(CancellationToken cancellation = default)
    {
        Claim[] claims;
        await _gate.WaitAsync(cancellation);
        try { claims = await Task.Run(() => Prepare(cancellation), cancellation); }
        finally { _gate.Release(); }
        var completed = await Task.WhenAll(claims.Select(claim => Run(claim, cancellation))); return completed.Sum();
    }
    private Claim[] Prepare(CancellationToken cancellation)
    {
        using var db = _store.Open(); using var tx = db.BeginTransaction(); var now = _clock.GetUtcNow();
        var plans = Plans(db, tx).ToDictionary(p => p.Info.Id);
        using (var expired = Command(db, tx, "SELECT id,plan_id,attempts,lease_owner,state FROM schedule_runs WHERE state IN ('running','cancel_requested') AND lease_until_utc<=$now", ("$now", Stamp(now))))
        {
            var rows = new List<(string Id, Guid Plan, int Attempts, string Owner, string State)>(); using (var reader = expired.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetInt32(2), reader.GetString(3), reader.GetString(4)));
            foreach (var row in rows)
            {
                if (row.Owner == _owner && _active.ContainsKey(row.Id)) continue;
                var policy = plans[row.Plan].Info.Execution; var retry = row.State != "cancel_requested" && row.Attempts <= policy.RetryCount;
                Execute(db, tx, "UPDATE schedule_runs SET state=$state,retry_utc=$retry,lease_owner=NULL,lease_until_utc=NULL,error='执行租约过期，结果未确认；相同投递 ID 重试' WHERE id=$id",
                    ("$state", row.State == "cancel_requested" ? "canceled" : retry ? "retry" : "failed"), ("$retry", retry ? Stamp(now + Backoff(policy, row.Attempts)) : null), ("$id", row.Id));
                if (!retry && plans[row.Plan].Info.Rule.Kind == RecurrenceKind.AfterCompletion) AdvanceAfterCompletion(db, tx, plans[row.Plan].Info, now);
            }
        }
        foreach (var entry in plans.Values.Where(p => p.Info.Enabled && p.Info.NextUtc <= now).OrderBy(p => p.Info.NextUtc).Take(100))
        {
            cancellation.ThrowIfCancellationRequested(); var plan = entry.Info; var policy = plan.Execution; var due = plan.NextUtc!.Value;
            if (plan.Rule.Kind == RecurrenceKind.AfterCompletion && Count(db, tx, "SELECT COUNT(*) FROM schedule_runs WHERE plan_id=$id AND state IN ('pending','retry','running','cancel_requested')", plan.Id) > 0) continue;
            var missed = now - due > policy.MisfireGrace;
            var oldest = now - policy.MaximumAge;
            if (due < oldest || missed && policy.Missed == MissedRunPolicy.Skip)
                InsertRun(db, tx, entry, due, "skipped", due < oldest ? "超出允许恢复窗口" : "错过时刻按配置跳过");
            var occurrences = new List<DateTimeOffset>();
            if (!(missed && policy.Missed == MissedRunPolicy.Skip))
            {
                var after = due < oldest ? oldest.AddTicks(-1) : due.AddTicks(-1);
                var count = missed && policy.Missed == MissedRunPolicy.LimitedCatchUp ? policy.CatchUpLimit : 1;
                occurrences.AddRange(_planner.Preview(plan.Rule, after, count, plan.LastCompletedUtc).Select(o => o.Utc).Where(t => t <= now && t >= oldest));
            }
            foreach (var occurrence in occurrences)
            {
                var live = Count(db, tx, "SELECT COUNT(*) FROM schedule_runs WHERE plan_id=$id AND state IN ('pending','retry','running','cancel_requested')", plan.Id);
                var skip = policy.Overlap == ScheduleOverlapPolicy.Skip && live > 0 || live >= policy.QueueLimit;
                InsertRun(db, tx, entry, occurrence, skip ? "skipped" : "pending", skip ? "重叠策略或队列上限跳过" : "");
            }
            if (plan.Rule.Kind != RecurrenceKind.AfterCompletion || occurrences.Count == 0)
            {
                var next = _planner.Preview(plan.Rule, now, 1, plan.LastCompletedUtc).FirstOrDefault()?.Utc;
                Execute(db, tx, "UPDATE schedules SET next_utc=$next WHERE id=$id", ("$next", Stamp(next)), ("$id", plan.Id.ToString()));
            }
        }
        var activeCounts = new Dictionary<Guid, int>(); var mutexes = new HashSet<string>(); var global = 0;
        using (var running = Command(db, tx, "SELECT plan_id FROM schedule_runs WHERE state IN ('running','cancel_requested')"))
        using (var reader = running.ExecuteReader()) while (reader.Read())
        { var id = Guid.Parse(reader.GetString(0)); activeCounts[id] = activeCounts.GetValueOrDefault(id) + 1; global++; if (plans[id].Info.Execution.Overlap == ScheduleOverlapPolicy.Mutex) mutexes.Add(Mutex(plans[id].Info)); }
        var pending = new List<(ReminderNotice Notice, int Attempts)>();
        using (var command = Command(db, tx, "SELECT r.payload,r.attempts FROM schedule_runs r JOIN schedules s ON r.plan_id=s.id WHERE s.enabled=1 AND r.state IN ('pending','retry') AND (r.retry_utc IS NULL OR r.retry_utc<=$now) ORDER BY r.due_utc,r.id LIMIT 500", ("$now", Stamp(now))))
        using (var reader = command.ExecuteReader()) while (reader.Read()) pending.Add((JsonSerializer.Deserialize<ReminderNotice>(reader.GetString(0))!, reader.GetInt32(1)));
        var claims = new List<Claim>();
        foreach (var item in pending)
        {
            if (global >= 32) break; var plan = plans[item.Notice.ReminderId].Info; var policy = plan.Execution;
            if (now - item.Notice.DueUtc > policy.MaximumAge)
            {
                Execute(db, tx, "UPDATE schedule_runs SET state='skipped',error='队列等待超过允许恢复窗口' WHERE id=$id", ("$id", item.Notice.DeliveryId));
                if (plan.Rule.Kind == RecurrenceKind.AfterCompletion) AdvanceAfterCompletion(db, tx, plan, now);
                continue;
            }
            var maximum = policy.Overlap == ScheduleOverlapPolicy.Parallel ? policy.MaxConcurrency : 1;
            if (activeCounts.GetValueOrDefault(plan.Id) >= maximum || policy.Overlap == ScheduleOverlapPolicy.Mutex && mutexes.Contains(Mutex(plan))) continue;
            Execute(db, tx, "UPDATE schedule_runs SET state='running',attempts=attempts+1,retry_utc=NULL,lease_owner=$owner,lease_until_utc=$until WHERE id=$id",
                ("$owner", _owner), ("$until", Stamp(now + policy.Timeout + TimeSpan.FromSeconds(5))), ("$id", item.Notice.DeliveryId));
            claims.Add(new(item.Notice, policy, item.Attempts + 1)); activeCounts[plan.Id] = activeCounts.GetValueOrDefault(plan.Id) + 1; global++;
            if (policy.Overlap == ScheduleOverlapPolicy.Mutex) mutexes.Add(Mutex(plan));
        }
        DisableFinished(db, tx); cancellation.ThrowIfCancellationRequested(); tx.Commit(); return claims.ToArray();
    }
    private async Task<int> Run(Claim claim, CancellationToken cancellation)
    {
        using var timeout = new CancellationTokenSource(claim.Policy.Timeout, _clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        if (!_active.TryAdd(claim.Notice.DeliveryId, (claim.Notice.ReminderId, stop))) return 0;
        var success = false; var error = ""; var timedOut = false;
        try
        {
            using (var db = _store.Open()) using (var state = Command(db, null, "SELECT state FROM schedule_runs WHERE id=$id", ("$id", claim.Notice.DeliveryId))) if (state.ExecuteScalar() as string != "running") stop.Cancel();
            stop.Token.ThrowIfCancellationRequested(); await _notifications.Publish(claim.Notice, stop.Token); stop.Token.ThrowIfCancellationRequested(); success = true;
        }
        catch (OperationCanceledException) { timedOut = timeout.IsCancellationRequested; error = timedOut ? "执行超时" : "执行取消或进程停止"; }
        catch (Exception e) { error = e.Message; }
        try
        {
            await Task.Run(() =>
            {
                using var db = _store.Open(); using var tx = db.BeginTransaction(); var now = _clock.GetUtcNow();
                using var find = Command(db, tx, "SELECT state FROM schedule_runs WHERE id=$id AND lease_owner=$owner", ("$id", claim.Notice.DeliveryId), ("$owner", _owner));
                var current = find.ExecuteScalar() as string; if (current is null) return;
                var canceled = current is "canceled" or "cancel_requested"; if (canceled) success = false;
                var retry = !success && !canceled && claim.Attempt <= claim.Policy.RetryCount;
                var state = canceled ? "canceled" : success ? "completed" : retry ? "retry" : timedOut ? "timedout" : "failed";
                Execute(db, tx, "UPDATE schedule_runs SET state=$state,error=$error,retry_utc=$retry,completed_utc=$completed,lease_owner=NULL,lease_until_utc=NULL WHERE id=$id",
                    ("$state", state), ("$error", canceled ? "用户取消或计划暂停" : error), ("$retry", retry ? Stamp(now + Backoff(claim.Policy, claim.Attempt)) : null), ("$completed", retry ? null : Stamp(now)), ("$id", claim.Notice.DeliveryId));
                Execute(db, tx, "UPDATE schedules SET error=$error WHERE id=$id", ("$error", error), ("$id", claim.Notice.ReminderId.ToString()));
                if (!retry)
                {
                    var plan = Plans(db, tx).Single(p => p.Info.Id == claim.Notice.ReminderId).Info;
                    Execute(db, tx, "UPDATE schedules SET last_completed_utc=$now WHERE id=$id", ("$now", Stamp(now)), ("$id", plan.Id.ToString()));
                    if (plan.Rule.Kind == RecurrenceKind.AfterCompletion && !canceled) AdvanceAfterCompletion(db, tx, plan, now);
                }
                DisableFinished(db, tx); tx.Commit();
            });
        }
        finally { _active.TryRemove(claim.Notice.DeliveryId, out _); }
        return success ? 1 : 0;
    }
    private void AdvanceAfterCompletion(SqliteConnection db, SqliteTransaction tx, ScheduleInfo plan, DateTimeOffset completed)
    {
        var next = _planner.Preview(plan.Rule, completed, 1, completed).FirstOrDefault()?.Utc;
        Execute(db, tx, "UPDATE schedules SET next_utc=$next,last_completed_utc=$done,enabled=CASE WHEN $next IS NULL THEN 0 ELSE enabled END WHERE id=$id", ("$next", Stamp(next)), ("$done", Stamp(completed)), ("$id", plan.Id.ToString()));
    }
    private static void InsertRun(SqliteConnection db, SqliteTransaction tx, StoredPlan plan, DateTimeOffset due, string state, string error)
    {
        var notice = new ReminderNotice("schedule:" + plan.Info.Id.ToString("N") + ":" + due.UtcTicks, plan.Info.Id, plan.Info.Title, due, null, plan.Mode);
        Execute(db, tx, "INSERT OR IGNORE INTO schedule_runs(id,plan_id,due_utc,state,error,payload) VALUES($id,$plan,$due,$state,$error,$payload)",
            ("$id", notice.DeliveryId), ("$plan", notice.ReminderId.ToString()), ("$due", Stamp(due)), ("$state", state), ("$error", error), ("$payload", JsonSerializer.Serialize(notice)));
    }
    private static StoredPlan[] Plans(SqliteConnection db, SqliteTransaction? tx)
    {
        using var command = Command(db, tx, "SELECT id,title,rule,next_utc,last_completed_utc,enabled,error,policy,mode FROM schedules ORDER BY enabled DESC,next_utc,id");
        using var reader = command.ExecuteReader(); var plans = new List<StoredPlan>();
        while (reader.Read()) plans.Add(new(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), JsonSerializer.Deserialize<RecurrenceRule>(reader.GetString(2))!, Instant(reader, 3), Instant(reader, 4), reader.GetBoolean(5), reader.GetString(6))
            { Execution = JsonSerializer.Deserialize<ScheduleExecutionPolicy>(reader.GetString(7))! }, (ReminderNotificationMode)reader.GetInt32(8)));
        return plans.ToArray();
    }
    private static string Mutex(ScheduleInfo plan) => string.IsNullOrWhiteSpace(plan.Execution.MutexKey) ? plan.Id.ToString() : plan.Execution.MutexKey.Trim();
    private static void DisableFinished(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, "UPDATE schedules SET enabled=0 WHERE next_utc IS NULL AND NOT EXISTS(SELECT 1 FROM schedule_runs WHERE plan_id=schedules.id AND state IN ('pending','retry','running','cancel_requested'))");
    private static TimeSpan Backoff(ScheduleExecutionPolicy policy, int attempt) => TimeSpan.FromMilliseconds(Math.Min(TimeSpan.FromHours(1).TotalMilliseconds, policy.RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1))));
    private static string? Stamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");
    private static DateTimeOffset? Instant(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : DateTimeOffset.Parse(reader.GetString(column));
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string text, params (string Name, object? Value)[] values)
    { var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = text; foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value); return command; }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string text, params (string Name, object? Value)[] values)
    { using var command = Command(db, tx, text, values); return command.ExecuteNonQuery(); }
    private static int Count(SqliteConnection db, SqliteTransaction tx, string text, Guid id)
    { using var command = Command(db, tx, text, ("$id", id.ToString())); return Convert.ToInt32(command.ExecuteScalar()); }
    private static void Validate(ScheduleExecutionPolicy p)
    {
        if (!Enum.IsDefined(p.Missed) || !Enum.IsDefined(p.Overlap) || p.CatchUpLimit is < 1 or > 100 || p.QueueLimit is < 1 or > 1000 || p.MaxConcurrency is < 1 or > 8 || p.RetryCount is < 0 or > 10) throw new InvalidDataException("补跑 1–100 次、队列 1–1000、并发 1–8、重试 0–10 次；策略须有效。");
        if (p.MaximumAge <= TimeSpan.Zero || p.MaximumAge > TimeSpan.FromDays(365) || p.Timeout <= TimeSpan.Zero || p.Timeout > TimeSpan.FromHours(1) || p.RetryDelay <= TimeSpan.Zero || p.RetryDelay > TimeSpan.FromHours(1) || p.MisfireGrace < TimeSpan.Zero || p.MisfireGrace > TimeSpan.FromHours(1)) throw new InvalidDataException("恢复窗口为 0–365 天，超时和重试间隔为 0–1 小时，迟到容忍为 0–1 小时。");
    }
}
