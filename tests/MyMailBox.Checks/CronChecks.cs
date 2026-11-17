using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class CronChecks
{
    public static async Task Run()
    {
        var planner = new RecurrencePlanner(); var rule = new RecurrenceRule { Kind = RecurrenceKind.Cron, Cron = "0 9 1 * MON", StartLocal = new(2026, 1, 1), TimeZoneId = "UTC" };
        var beginning = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var combined = planner.Preview(rule, beginning); Check(combined[0].Utc == new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), "CRON DOM/DOW must use AND");
        Check(planner.Preview(rule with { Cron = "0 0 29 2 *" }, beginning)[0].Local == new DateTime(2028, 2, 29), "CRON leap year wrong");
        Check(planner.Preview(rule with { Cron = "0 0 31 12 *" }, new(2026, 12, 31, 12, 0, 0, TimeSpan.Zero))[0].Local.Year == 2027, "CRON cross-year wrong");
        var sunday0 = planner.Preview(rule with { Cron = "0 9 * * 0" }, beginning); var sunday7 = planner.Preview(rule with { Cron = "0 9 * * 7" }, beginning);
        Check(sunday0.SequenceEqual(sunday7), "Sunday 0/7 inconsistent");
        foreach (var invalid in new[] { "0 0 9 * * *", "61 9 * * *", "@daily", "H 9 * * *", "x 9 * * *" })
        { try { planner.Preview(rule with { Cron = invalid }, beginning); throw new Exception("Invalid cron accepted: " + invalid); } catch (InvalidDataException e) { Check(e.Message.Contains("CRON") || e.Message.Contains("方言"), "CRON diagnostic missing"); } }
        var eastern = rule with { TimeZoneId = "Eastern Standard Time", Cron = "30 2 * * *", StartLocal = new(2026, 3, 1) };
        var spring = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);
        Check(planner.Preview(eastern, spring)[0].Local.Date == new DateTime(2026, 3, 9), "Missing DST time not skipped");
        var shifted = planner.Preview(eastern with { MissingTime = MissingTimePolicy.NextValidMinute }, spring)[0];
        Check(shifted.Local == new DateTime(2026, 3, 8, 3, 0, 0) && shifted.Utc.Hour == 7, "Missing DST time not moved to first valid minute");
        var daily = planner.Preview(eastern with { Kind = RecurrenceKind.Daily, At = new(2, 30), MissingTime = MissingTimePolicy.NextValidMinute }, spring)[0];
        Check(daily == shifted, "Form/CRON DST policy differs");
        var fallRule = eastern with { Cron = "30 1 * * *", StartLocal = new(2026, 10, 1) }; var fall = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var early = planner.Preview(fallRule, fall)[0]; var late = planner.Preview(fallRule with { RepeatedTime = RepeatedTimePolicy.Later }, fall)[0];
        Check(early.Utc.Hour == 5 && late.Utc.Hour == 6 && early.Local == late.Local, "Repeated DST time policy wrong");
        var lateAfterFirstFold = planner.Preview(fallRule with { RepeatedTime = RepeatedTimePolicy.Later }, fall.AddHours(5).AddMinutes(45))[0];
        Check(lateAfterFirstFold.Utc == late.Utc, "Later fold was lost after first fold wall time");
        var fixedInterval = planner.Preview(fallRule with { Kind = RecurrenceKind.FixedInterval, Interval = TimeSpan.FromHours(1) }, fall, 10);
        Check(fixedInterval.Zip(fixedInterval.Skip(1)).All(p => p.Second.Utc - p.First.Utc == TimeSpan.FromHours(1)), "Fixed UTC interval changed around DST");
        var root = Path.Combine(Path.GetTempPath(), "MyMailBox-cron-" + Guid.NewGuid().ToString("N")); var store = new SqliteMailStore(root); var clock = new Clock(fall);
        var shown = new List<ReminderNotice>(); var service = new ScheduleService(store, planner, new SqliteNotificationSink(store, shown.Add), clock);
        var created = await service.Create(new("较晚时刻提醒", fallRule with { RepeatedTime = RepeatedTimePolicy.Later }));
        Check(created.NextUtc == late.Utc, "Persisted cron differs from preview");
        clock.Now = early.Utc; await service.Tick(); Check(shown.Count == 0, "Later policy fired on first fold");
        clock.Now = late.Utc; await service.Tick(); await service.Tick(); Check(shown.Count == 1, "Later policy did not fire exactly once");
        var persisted = (await new ScheduleService(store, planner, new SqliteNotificationSink(store, shown.Add), clock).List()).Single();
        Check(persisted.Rule.TimeZoneId == "Eastern Standard Time" && persisted.NextUtc == planner.Preview(persisted.Rule, late.Utc, 1)[0].Utc, "Restart/host zone changed saved semantics");
        Console.WriteLine("PASS CRON: pinned five-field dialect, DOM/DOW AND, Sunday aliases, diagnostics, leap/year transitions, gap/fold policies shared with forms, fixed duration DST, preview/execution/restart consistency");
    }
    private static void Check(bool value, string message) => MailChecks.Check(value, message);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
}
