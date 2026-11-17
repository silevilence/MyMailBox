namespace MyMailBox.Contracts;

public enum ReminderNotificationMode { Banner, ListOnly }
public sealed record ReminderDraft(string Title, DateTime LocalTime, string TimeZoneId, Guid? MessageId = null, ReminderNotificationMode Notification = ReminderNotificationMode.Banner);
public sealed record ReminderInfo(Guid Id, string Title, DateTimeOffset DueUtc, string TimeZoneId, Guid? MessageId,
    string MessageState, string State, ReminderNotificationMode Notification, string Error);
public sealed record ReminderNotice(string DeliveryId, Guid ReminderId, string Title, DateTimeOffset DueUtc, Guid? MessageId, ReminderNotificationMode Mode);
public interface INotificationSink
{
    // A durable delivery ID must be idempotent, including after a process restart.
    Task Publish(ReminderNotice notice, CancellationToken cancellation = default);
    Task Dismiss(string deliveryId, CancellationToken cancellation = default);
}
public interface IReminderService
{
    Task<ReminderInfo> Create(ReminderDraft draft, CancellationToken cancellation = default);
    Task<IReadOnlyList<ReminderInfo>> List(CancellationToken cancellation = default);
    Task Snooze(Guid id, TimeSpan delay, CancellationToken cancellation = default);
    Task Dismiss(Guid id, CancellationToken cancellation = default);
    Task Cancel(Guid id, CancellationToken cancellation = default);
    Task<int> Tick(CancellationToken cancellation = default);
}
public sealed record ReminderPage(IReminderService Scheduler, IMailStore Mail);
