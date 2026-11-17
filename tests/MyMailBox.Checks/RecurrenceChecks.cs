using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class RecurrenceChecks
{
    public static async Task Run()
    {
        var planner = new RecurrencePlanner(); var start = new DateTime(2026, 1, 1); var after = new DateTimeOffset(start, TimeSpan.Zero);
        var rule = new RecurrenceRule { TimeZoneId = "UTC", StartLocal = start, Kind = RecurrenceKind.Daily, At = new(9, 30), Every = 2 };
        var daily = planner.Preview(rule, after); Check(daily.Count == 10 && daily[1].Local == new DateTime(2026, 1, 3, 9, 30, 0), "Daily anchor interval wrong");
        var weekly = planner.Preview(rule with { Kind = RecurrenceKind.Weekly, Every = 1, Weekdays = [DayOfWeek.Monday, DayOfWeek.Friday] }, after);
        Check(weekly[0].Local.Date == new DateTime(2026, 1, 2) && weekly[1].Local.Date == new DateTime(2026, 1, 5), "Weekly multiple days wrong");
        var monthly = rule with { Kind = RecurrenceKind.MonthlyDate, Every = 1, Day = 31 };
        var skip = planner.Preview(monthly, after); Check(skip[0].Local.Month == 1 && skip[1].Local.Month == 3, "Missing day did not skip February");
        var clamp = planner.Preview(monthly with { MissingDate = MissingDatePolicy.LastValid }, after); Check(clamp[1].Local.Date == new DateTime(2026, 2, 28), "Month end adjustment wrong");
        var relative = rule with { Kind = RecurrenceKind.MonthlyWeekday, Every = 1, Ordinal = 5, Weekday = DayOfWeek.Monday };
        Check(planner.Preview(relative, after)[0].Local.Date == new DateTime(2026, 3, 30), "Missing fifth weekday did not skip");
        Check(planner.Preview(relative with { MissingDate = MissingDatePolicy.LastValid }, after)[0].Local.Date == new DateTime(2026, 1, 26), "Fifth weekday adjustment wrong");
        Check(planner.Preview(relative with { Ordinal = -1 }, after)[1].Local.Date == new DateTime(2026, 2, 23), "Last weekday wrong");
        var yearly = rule with { Kind = RecurrenceKind.Yearly, Every = 1, Month = 2, Day = 29 };
        var leap = planner.Preview(yearly, after); Check(leap.Count == 10 && leap[0].Local.Year == 2028 && leap[1].Local.Year == 2032, "Yearly leap day wrong");
        Check(planner.Preview(yearly with { MissingDate = MissingDatePolicy.LastValid }, after)[0].Local.Date == new DateTime(2026, 2, 28), "Yearly adjustment wrong");
        var fixedRule = rule with { Kind = RecurrenceKind.FixedInterval, Interval = TimeSpan.FromHours(3) };
        Check(planner.Preview(fixedRule, after.AddHours(4))[0].Utc == after.AddHours(6), "Fixed interval reset anchor");
        var completion = after.AddHours(4).AddMinutes(7);
        Check(planner.Preview(fixedRule with { Kind = RecurrenceKind.AfterCompletion }, completion, completed: completion)[0].Utc == completion.AddHours(3), "Completion interval anchored to start instead of completion");
        var limited = rule with { Every = 1, EndLocal = new(2026, 1, 10, 10, 0, 0), ExcludedDates = [new(2026, 1, 2)], CalendarId = "weekdays", WindowStart = new(9, 0), WindowEnd = new(10, 0) };
        var allowed = planner.Preview(limited, after); Check(allowed.Count == 6 && allowed.All(o => o.Local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && o.Local.Day != 2), "Window/exclusion/calendar/end constraints failed");
        var holiday = new RecurrencePlanner([new HolidayCalendar()]); var holidayDates = holiday.Preview(rule with { Every = 1, CalendarId = "test-holidays" }, after);
        Check(holidayDates[0].Local.Day == 2, "Replaceable holiday calendar ignored");
        try { planner.Preview(rule with { CalendarId = "missing" }, after); throw new Exception("Missing calendar accepted"); } catch (InvalidDataException) { }
        var root = Path.Combine(Path.GetTempPath(), "MyMailBox-recurrence-" + Guid.NewGuid().ToString("N")); var store = new SqliteMailStore(root);
        var clock = new Clock(after); var notices = new List<ReminderNotice>(); var sink = new AdvancingSink(new SqliteNotificationSink(store, notices.Add), clock);
        var service = new ScheduleService(store, planner, sink, clock);
        var plan = await service.Create(new("固定循环", fixedRule)); var deferred = await service.Create(new("完成后循环", fixedRule with { Kind = RecurrenceKind.AfterCompletion }));
        await service.Tick(); Check(notices.Count == 2, "Initial occurrence not dispatched");
        var plans = await service.List();
        Check(plans.Single(p => p.Id == plan.Id).NextUtc == after.AddHours(3), "Actual fixed scheduling disagrees with preview");
        var completedPlan = plans.Single(p => p.Id == deferred.Id); Check(completedPlan.NextUtc == completedPlan.LastCompletedUtc!.Value.AddHours(3), "Actual completion interval wrong");
        var reopened = new ScheduleService(new SqliteMailStore(root), planner, sink, clock); var reopenedPlan = (await reopened.List()).Single(p => p.Id == plan.Id);
        Check(reopenedPlan.Rule.StartLocal == start && reopenedPlan.NextUtc == after.AddHours(3), "Restart reset cycle anchor");
        await reopened.Tick(); Check(notices.Count == 2, "Restart duplicated completed occurrence");
        await reopened.SetEnabled(plan.Id, false); clock.Advance(TimeSpan.FromHours(4)); await reopened.Tick(); Check(notices.Count(n => n.ReminderId == plan.Id) == 1, "Paused schedule executed");
        await reopened.SetEnabled(plan.Id, true); Check((await reopened.List()).Single(p => p.Id == plan.Id).NextUtc == after.AddHours(6), "Resume reset fixed anchor");
        Console.WriteLine("PASS recurrence: 10-occurrence daily/weekly/month-end/nth-weekday/yearly previews, explicit missing dates, fixed vs completion intervals, windows/exclusions/calendar, persisted execution/restart/resume anchors");
    }
    private static void Check(bool condition, string message) => MailChecks.Check(condition, message);
    private sealed class HolidayCalendar : IWorkCalendar { public string Id => "test-holidays"; public string Name => "测试节假日"; public bool Allows(DateOnly date) => date != new DateOnly(2026, 1, 1); }
    private sealed class Clock(DateTimeOffset current) : TimeProvider { public override DateTimeOffset GetUtcNow() => current; public void Advance(TimeSpan span) => current += span; }
    private sealed class AdvancingSink(INotificationSink inner, Clock clock) : INotificationSink
    {
        public async Task Publish(ReminderNotice notice, CancellationToken cancellation = default) { await inner.Publish(notice, cancellation); clock.Advance(TimeSpan.FromMinutes(2)); }
        public Task Dismiss(string id, CancellationToken cancellation = default) => inner.Dismiss(id, cancellation);
    }
}
