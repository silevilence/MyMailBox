namespace MyMailBox.Contracts;

public enum RecurrenceKind { FixedInterval, AfterCompletion, Daily, Weekly, MonthlyDate, MonthlyWeekday, Yearly }
public enum MissingDatePolicy { Skip, LastValid }
public enum MissingTimePolicy { Skip, NextValidMinute }
public enum RepeatedTimePolicy { Earlier, Later }
public sealed record RecurrenceRule
{
    public int Version { get; init; } = 1;
    public RecurrenceKind Kind { get; init; } = RecurrenceKind.Daily;
    public int Every { get; init; } = 1;
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
    public DateTime StartLocal { get; init; } = DateTime.Today;
    public DateTime? EndLocal { get; init; }
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    public TimeOnly At { get; init; } = new(9, 0);
    public DayOfWeek[] Weekdays { get; init; } = [DayOfWeek.Monday];
    public int Day { get; init; } = 1;
    public int Month { get; init; } = 1;
    public int Ordinal { get; init; } = 1;
    public DayOfWeek Weekday { get; init; } = DayOfWeek.Monday;
    public MissingDatePolicy MissingDate { get; init; } = MissingDatePolicy.Skip;
    public MissingTimePolicy MissingTime { get; init; } = MissingTimePolicy.Skip;
    public RepeatedTimePolicy RepeatedTime { get; init; } = RepeatedTimePolicy.Earlier;
    public TimeOnly? WindowStart { get; init; }
    public TimeOnly? WindowEnd { get; init; }
    public DateOnly[] ExcludedDates { get; init; } = [];
    public string CalendarId { get; init; } = "all";
}
public interface IWorkCalendar
{
    string Id { get; }
    string Name { get; }
    bool Allows(DateOnly date);
}
public sealed record CalendarChoice(string Id, string Name);
public sealed record ScheduleOccurrence(DateTimeOffset Utc, DateTime Local, string Zone);
public interface ISchedulePlanner
{
    IReadOnlyList<CalendarChoice> Calendars { get; }
    IReadOnlyList<ScheduleOccurrence> Preview(RecurrenceRule rule, DateTimeOffset after, int count = 10, DateTimeOffset? completed = null);
}
public sealed record ScheduleDraft(string Title, RecurrenceRule Rule, ReminderNotificationMode Notification = ReminderNotificationMode.Banner);
public sealed record ScheduleInfo(Guid Id, string Title, RecurrenceRule Rule, DateTimeOffset? NextUtc, DateTimeOffset? LastCompletedUtc, bool Enabled, string Error);
public interface IScheduleService
{
    IReadOnlyList<CalendarChoice> Calendars { get; }
    Task<IReadOnlyList<ScheduleOccurrence>> Preview(RecurrenceRule rule, CancellationToken cancellation = default);
    Task<ScheduleInfo> Create(ScheduleDraft draft, CancellationToken cancellation = default);
    Task<IReadOnlyList<ScheduleInfo>> List(CancellationToken cancellation = default);
    Task SetEnabled(Guid id, bool enabled, CancellationToken cancellation = default);
    Task<int> Tick(CancellationToken cancellation = default);
}
