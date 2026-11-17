namespace MyMailBox.Contracts;

public enum RecurrenceKind { FixedInterval, AfterCompletion, Daily, Weekly, MonthlyDate, MonthlyWeekday, Yearly, Cron }
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
    public string Cron { get; init; } = "0 9 * * MON-FRI";
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
public enum MissedRunPolicy { Skip, Once, LimitedCatchUp }
public enum ScheduleOverlapPolicy { Skip, Queue, Mutex, Parallel }
public sealed record ScheduleExecutionPolicy
{
    public MissedRunPolicy Missed { get; init; } = MissedRunPolicy.Once;
    public int CatchUpLimit { get; init; } = 3;
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan MisfireGrace { get; init; } = TimeSpan.FromSeconds(5);
    public ScheduleOverlapPolicy Overlap { get; init; } = ScheduleOverlapPolicy.Queue;
    public string MutexKey { get; init; } = "";
    public int MaxConcurrency { get; init; } = 2;
    public int QueueLimit { get; init; } = 100;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int RetryCount { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}
public sealed record ScheduleDraft(string Title, RecurrenceRule Rule, ReminderNotificationMode Notification = ReminderNotificationMode.Banner)
{
    public ScheduleExecutionPolicy Execution { get; init; } = new();
}
public sealed record ScheduleInfo(Guid Id, string Title, RecurrenceRule Rule, DateTimeOffset? NextUtc, DateTimeOffset? LastCompletedUtc, bool Enabled, string Error)
{
    public ScheduleExecutionPolicy Execution { get; init; } = new();
}
public sealed record ScheduleRunInfo(string Id, Guid PlanId, DateTimeOffset DueUtc, string State, int Attempts, DateTimeOffset? RetryUtc, DateTimeOffset? CompletedUtc, string Error);
public interface IScheduleService
{
    IReadOnlyList<CalendarChoice> Calendars { get; }
    Task<IReadOnlyList<ScheduleOccurrence>> Preview(RecurrenceRule rule, CancellationToken cancellation = default);
    Task<ScheduleInfo> Create(ScheduleDraft draft, CancellationToken cancellation = default);
    Task<IReadOnlyList<ScheduleInfo>> List(CancellationToken cancellation = default);
    Task SetEnabled(Guid id, bool enabled, CancellationToken cancellation = default);
    Task<IReadOnlyList<ScheduleRunInfo>> History(Guid id, CancellationToken cancellation = default);
    Task CancelRun(string id, CancellationToken cancellation = default);
    Task<int> Tick(CancellationToken cancellation = default);
}
