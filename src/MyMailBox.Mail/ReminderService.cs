using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ReminderService : IReminderService
{
    private readonly SqliteMailStore _store;
    private readonly INotificationSink _notifications;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1);
    public ReminderService(SqliteMailStore store, INotificationSink notifications, TimeProvider? clock = null)
    {
        _store = store; _notifications = notifications; _clock = clock ?? TimeProvider.System;
        _ = new AccountService(store);
        using var db = store.Open(); using var command = db.CreateCommand(); command.CommandText = """
            CREATE TABLE IF NOT EXISTS reminders(id TEXT PRIMARY KEY,title TEXT NOT NULL,due_utc TEXT NOT NULL,time_zone TEXT NOT NULL,message_id TEXT,state TEXT NOT NULL,mode INTEGER NOT NULL,revision INTEGER NOT NULL DEFAULT 0,error TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS reminders_due ON reminders(state,due_utc);
            CREATE TABLE IF NOT EXISTS reminder_outbox(delivery_id TEXT PRIMARY KEY,reminder_id TEXT NOT NULL,payload TEXT NOT NULL,delivered INTEGER NOT NULL DEFAULT 0);
            """; command.ExecuteNonQuery();
    }
    public async Task<ReminderInfo> Create(ReminderDraft draft, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200) throw new InvalidDataException("提醒内容需为 1–200 字。");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(draft.TimeZoneId); var local = DateTime.SpecifyKind(draft.LocalTime, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) throw new InvalidDataException("该时刻在此时区因夏令时跳过，请选择存在的时间。");
        if (zone.IsAmbiguousTime(local)) throw new InvalidDataException("该时刻因夏令时重复，请使用 UTC 时区明确选择其中一次。");
        var due = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)); var id = Guid.NewGuid();
        await Task.Run(() =>
        {
            using var db = _store.Open(); using var command = db.CreateCommand();
            if (draft.MessageId is { } message)
            {
                command.CommandText = "SELECT COUNT(*) FROM messages WHERE id=$message"; command.Parameters.AddWithValue("$message", message.ToString());
                if (Convert.ToInt32(command.ExecuteScalar()) == 0) throw new InvalidDataException("关联邮件已不存在，请刷新后重新选择。");
                command.Parameters.Clear();
            }
            command.CommandText = "INSERT INTO reminders(id,title,due_utc,time_zone,message_id,state,mode) VALUES($id,$title,$due,$zone,$message,'pending',$mode)";
            command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$title", draft.Title.Trim()); command.Parameters.AddWithValue("$due", due.ToString("O")); command.Parameters.AddWithValue("$zone", zone.Id);
            command.Parameters.AddWithValue("$message", draft.MessageId?.ToString() is { } value ? value : DBNull.Value); command.Parameters.AddWithValue("$mode", (int)draft.Notification);
            cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
        }, cancellation);
        return (await List(cancellation)).Single(r => r.Id == id);
    }
    public Task<IReadOnlyList<ReminderInfo>> List(CancellationToken cancellation = default) => Task.Run<IReadOnlyList<ReminderInfo>>(() =>
    {
        using var db = _store.Open(); using var command = db.CreateCommand();
        command.CommandText = """
            SELECT r.*,m.subject,f.path,a.name,a.enabled,a.removed FROM reminders r
            LEFT JOIN messages m ON m.id=r.message_id LEFT JOIN folders f ON f.id=m.folder_id LEFT JOIN accounts a ON a.id=f.account_id
            ORDER BY CASE r.state WHEN 'notified' THEN 0 WHEN 'pending' THEN 1 ELSE 2 END,r.due_utc,r.id
            """;
        using var reader = command.ExecuteReader(); var reminders = new List<ReminderInfo>();
        while (reader.Read())
        {
            cancellation.ThrowIfCancellationRequested();
            var message = reader.IsDBNull(4) ? (Guid?)null : Guid.Parse(reader.GetString(4));
            var reference = message is null ? "自定义事项" : reader.IsDBNull(9) ? "关联邮件已删除或不可用" : reader.IsDBNull(12) || !reader.GetBoolean(12) || reader.GetBoolean(13) ? "关联邮箱已关闭/移除，原文保留" : reader.GetString(11) + " · " + reader.GetString(10) + " · " + reader.GetString(9);
            reminders.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)), reader.GetString(3), message, reference, reader.GetString(5), (ReminderNotificationMode)reader.GetInt32(6), reader.GetString(8)));
        }
        return reminders;
    }, cancellation);
    public Task Snooze(Guid id, TimeSpan delay, CancellationToken cancellation = default)
    {
        if (delay <= TimeSpan.Zero || delay > TimeSpan.FromDays(365)) throw new InvalidDataException("延后时长需在 0 到 365 天之间。");
        return Change(id, "pending", _clock.GetUtcNow() + delay, cancellation);
    }
    public Task Dismiss(Guid id, CancellationToken cancellation = default) => Change(id, "dismissed", null, cancellation);
    public Task Cancel(Guid id, CancellationToken cancellation = default) => Change(id, "canceled", null, cancellation);
    private async Task Change(Guid id, string state, DateTimeOffset? due, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var delivery = await Task.Run(() =>
            {
                using var db = _store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT revision FROM reminders WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
                var revision = command.ExecuteScalar() ?? throw new InvalidDataException("提醒不存在。"); var key = id.ToString("N") + ":" + revision;
                command.CommandText = "UPDATE reminders SET state=$state,due_utc=COALESCE($due,due_utc),revision=revision+1,error='' WHERE id=$id; DELETE FROM reminder_outbox WHERE reminder_id=$id AND delivered=0;";
                command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$due", due?.ToUniversalTime().ToString("O") is { } value ? value : DBNull.Value);
                cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); transaction.Commit(); return key;
            }, cancellation);
            await _notifications.Dismiss(delivery, cancellation);
        }
        finally { _gate.Release(); }
    }
    public async Task<int> Tick(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var pending = await Task.Run(() =>
            {
                using var db = _store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT id,title,due_utc,message_id,mode,revision FROM reminders WHERE state='pending' AND due_utc<=$now ORDER BY due_utc LIMIT 100";
                command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUniversalTime().ToString("O")); var due = new List<ReminderNotice>();
                using (var reader = command.ExecuteReader()) while (reader.Read())
                {
                    var id = Guid.Parse(reader.GetString(0)); due.Add(new(id.ToString("N") + ":" + reader.GetInt32(5), id, reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), (ReminderNotificationMode)reader.GetInt32(4)));
                }
                foreach (var notice in due)
                {
                    cancellation.ThrowIfCancellationRequested(); command.Parameters.Clear();
                    command.CommandText = "INSERT OR IGNORE INTO reminder_outbox(delivery_id,reminder_id,payload) VALUES($delivery,$id,$payload); UPDATE reminders SET state='notified' WHERE id=$id";
                    command.Parameters.AddWithValue("$delivery", notice.DeliveryId); command.Parameters.AddWithValue("$id", notice.ReminderId.ToString()); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(notice)); command.ExecuteNonQuery();
                }
                command.Parameters.Clear(); command.CommandText = "SELECT payload FROM reminder_outbox WHERE delivered=0 LIMIT 100"; var outbox = new List<ReminderNotice>();
                using (var reader = command.ExecuteReader()) while (reader.Read()) outbox.Add(JsonSerializer.Deserialize<ReminderNotice>(reader.GetString(0))!);
                cancellation.ThrowIfCancellationRequested(); transaction.Commit(); return outbox;
            }, cancellation);
            var delivered = 0;
            foreach (var notice in pending)
            {
                cancellation.ThrowIfCancellationRequested(); string error = "";
                try { await _notifications.Publish(notice, cancellation); delivered++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { error = e.Message; }
                await Task.Run(() =>
                {
                    using var db = _store.Open(); using var command = db.CreateCommand();
                    command.CommandText = "UPDATE reminder_outbox SET delivered=$delivered WHERE delivery_id=$delivery; UPDATE reminders SET error=$error WHERE id=$id";
                    command.Parameters.AddWithValue("$delivered", error.Length == 0 ? 1 : 0); command.Parameters.AddWithValue("$delivery", notice.DeliveryId); command.Parameters.AddWithValue("$error", error); command.Parameters.AddWithValue("$id", notice.ReminderId.ToString()); command.ExecuteNonQuery();
                }, cancellation);
            }
            return delivered;
        }
        finally { _gate.Release(); }
    }
}
