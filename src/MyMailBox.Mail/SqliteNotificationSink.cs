using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class SqliteNotificationSink : INotificationSink
{
    private readonly SqliteMailStore _store;
    private readonly Action<ReminderNotice>? _show;
    public SqliteNotificationSink(SqliteMailStore store, Action<ReminderNotice>? show = null)
    {
        _store = store; _show = show;
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS notification_inbox(delivery_id TEXT PRIMARY KEY,payload TEXT NOT NULL,dismissed INTEGER NOT NULL DEFAULT 0)"; command.ExecuteNonQuery();
    }
    public async Task Publish(ReminderNotice notice, CancellationToken cancellation = default)
    {
        var inserted = await Task.Run(() =>
        {
            using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT OR IGNORE INTO notification_inbox(delivery_id,payload) VALUES($id,$payload)";
            command.Parameters.AddWithValue("$id", notice.DeliveryId); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(notice)); cancellation.ThrowIfCancellationRequested(); return command.ExecuteNonQuery() == 1;
        }, cancellation);
        if (inserted && notice.Mode == ReminderNotificationMode.Banner) _show?.Invoke(notice);
    }
    public Task Dismiss(string deliveryId, CancellationToken cancellation = default) => Task.Run(() =>
    {
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE notification_inbox SET dismissed=1 WHERE delivery_id=$id"; command.Parameters.AddWithValue("$id", deliveryId);
        cancellation.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
    }, cancellation);
}
