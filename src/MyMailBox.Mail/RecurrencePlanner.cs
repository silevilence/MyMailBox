using MyMailBox.Contracts;
using Cronos;

namespace MyMailBox.Mail;

public sealed class RecurrencePlanner : ISchedulePlanner
{
    private readonly IReadOnlyDictionary<string, IWorkCalendar> _calendars;
    public RecurrencePlanner(IEnumerable<IWorkCalendar>? calendars = null)
    {
        var items = calendars?.ToArray() ?? [new AllDaysCalendar(), new WeekdaysCalendar()]; _calendars = items.ToDictionary(c => c.Id);
        Calendars = items.Select(c => new CalendarChoice(c.Id, c.Name)).ToArray();
    }
    public IReadOnlyList<CalendarChoice> Calendars { get; }
    public IReadOnlyList<ScheduleOccurrence> Preview(RecurrenceRule rule, DateTimeOffset after, int count = 10, DateTimeOffset? completed = null)
    {
        Validate(rule); if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZoneId); var calendar = _calendars[rule.CalendarId]; var result = new List<ScheduleOccurrence>();
        var start = DateTime.SpecifyKind(rule.StartLocal, DateTimeKind.Unspecified); var end = rule.EndLocal is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Unspecified) : DateTime.MaxValue;
        var excluded = rule.ExcludedDates.ToHashSet();
        bool Allowed(DateTime local)
        {
            var date = DateOnly.FromDateTime(local); var time = TimeOnly.FromDateTime(local);
            return local >= start && local <= end && !excluded.Contains(date) && calendar.Allows(date) &&
                (rule.WindowStart is null || time >= rule.WindowStart) && (rule.WindowEnd is null || time <= rule.WindowEnd);
        }
        if (rule.Kind == RecurrenceKind.Cron)
        {
            var expression = ParseCron(rule.Cron);
            // Enumerate wall-clock candidates, then apply the exact same DST policy as forms.
            // Starting at local midnight also catches the later fold occurrence after an earlier fold time.
            var localAfter = TimeZoneInfo.ConvertTime(after, zone).DateTime;
            var cursor = DateTime.SpecifyKind((localAfter > start ? localAfter : start).Date, DateTimeKind.Utc);
            var inclusive = true;
            for (var attempts = 0; result.Count < count && attempts < 100_000; attempts++)
            {
                var candidate = expression.GetNextOccurrence(cursor, inclusive); inclusive = false;
                if (candidate is null) break; cursor = candidate.Value;
                var local = DateTime.SpecifyKind(cursor, DateTimeKind.Unspecified); if (local > end) break;
                if (!Allowed(local)) continue;
                var utc = Resolve(local, zone, rule);
                if (utc is { } instant && instant > after && Allowed(TimeZoneInfo.ConvertTime(instant, zone).DateTime) && !result.Any(o => o.Utc == instant))
                    result.Add(new(instant, TimeZoneInfo.ConvertTime(instant, zone).DateTime, zone.Id));
            }
            return result;
        }
        if (rule.Kind is RecurrenceKind.FixedInterval or RecurrenceKind.AfterCompletion)
        {
            var anchor = Resolve(start, zone, rule) ?? throw new InvalidDataException("周期锚点落在不存在的本地时刻，请调整开始时间。");
            var next = anchor;
            if (rule.Kind == RecurrenceKind.AfterCompletion)
            {
                if (completed is { } done) next = done + rule.Interval;
                if (next <= after) next = after + rule.Interval;
            }
            else if (next <= after) next += TimeSpan.FromTicks(((after - next).Ticks / rule.Interval.Ticks + 1) * rule.Interval.Ticks);
            // The limit bounds pathological calendars while covering ordinary windows and exclusions.
            for (var attempts = 0; result.Count < count && attempts < 100_000; attempts++, next += rule.Interval)
            {
                var local = TimeZoneInfo.ConvertTime(next, zone).DateTime;
                if (local > end) break;
                if (next > after && Allowed(local)) result.Add(new(next, local, zone.Id));
            }
            if (result.Count == 0 && end == DateTime.MaxValue) throw new InvalidDataException("未来 100000 个周期点均不符合日历/窗口限制，请调整范围。");
            return result;
        }
        var firstDate = DateOnly.FromDateTime(start); var afterDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        var dateToCheck = afterDate > firstDate ? afterDate : firstDate;
        var horizon = Math.Min(DateOnly.MaxValue.DayNumber, dateToCheck.DayNumber + 366 * 100);
        for (; dateToCheck.DayNumber <= horizon && result.Count < count; dateToCheck = dateToCheck.AddDays(1))
        {
            var local = dateToCheck.ToDateTime(rule.At, DateTimeKind.Unspecified); if (local > end) break;
            if (!Matches(rule, firstDate, dateToCheck) || !Allowed(local)) continue;
            var utc = Resolve(local, zone, rule);
            if (utc is { } instant && instant > after)
            {
                var resolved = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
                if (Allowed(resolved)) result.Add(new(instant, resolved, zone.Id));
            }
            if (dateToCheck == DateOnly.MaxValue) break;
        }
        return result;
    }
    private void Validate(RecurrenceRule rule)
    {
        if (rule.Version != 1) throw new InvalidDataException("不支持的周期规则版本。");
        if (!Enum.IsDefined(rule.Kind) || rule.Every is < 1 or > 100) throw new InvalidDataException("周期倍数需为 1–100。");
        if (rule.Interval < TimeSpan.FromMinutes(1) || rule.Interval > TimeSpan.FromDays(365)) throw new InvalidDataException("固定/完成后间隔需为 1 分钟到 365 天。");
        if (rule.EndLocal < rule.StartLocal) throw new InvalidDataException("结束时间不能早于开始时间。");
        if (rule.WindowStart is not null != (rule.WindowEnd is not null) || rule.WindowStart > rule.WindowEnd) throw new InvalidDataException("运行窗口需同时填写开始/结束，且为同一天内的正向范围。");
        if (rule.Day is < 1 or > 31 || rule.Month is < 1 or > 12 || rule.Ordinal is not (-1 or >= 1 and <= 5)) throw new InvalidDataException("日期需为 1–31、月份为 1–12、第几周需为 1–5 或最后一次。");
        if (rule.Kind == RecurrenceKind.Weekly && rule.Weekdays.Length == 0) throw new InvalidDataException("每周计划至少选择一个星期。");
        if (!_calendars.ContainsKey(rule.CalendarId)) throw new InvalidDataException("工作日/节假日日历插件不可用：" + rule.CalendarId);
        if (rule.Kind == RecurrenceKind.Cron) _ = ParseCron(rule.Cron);
    }
    private static CronExpression ParseCron(string expression)
    {
        if (expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length != 5) throw new InvalidDataException("CRON 需为 5 字段：分 时 日 月 星期；不包含秒或年份。");
        if (System.Text.RegularExpressions.Regex.IsMatch(expression, @"(?<![A-Z])H(?![A-Z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new InvalidDataException("当前方言不支持 H 随机抖动，请使用确定的分钟/小时。");
        try { return CronExpression.Parse(expression, CronFormat.Standard); }
        catch (CronFormatException e) { throw new InvalidDataException("CRON 字段无效：" + e.Message, e); }
    }
    private static bool Matches(RecurrenceRule rule, DateOnly start, DateOnly date)
    {
        var months = (date.Year - start.Year) * 12 + date.Month - start.Month;
        return rule.Kind switch
        {
            RecurrenceKind.Daily => (date.DayNumber - start.DayNumber) % rule.Every == 0,
            RecurrenceKind.Weekly => ((date.DayNumber - Monday(start).DayNumber) / 7) % rule.Every == 0 && rule.Weekdays.Contains(date.DayOfWeek),
            RecurrenceKind.MonthlyDate => months % rule.Every == 0 && date.Day == ValidDay(rule.Day, date.Year, date.Month, rule.MissingDate),
            RecurrenceKind.MonthlyWeekday => months % rule.Every == 0 && date.Day == WeekdayInMonth(date.Year, date.Month, rule.Weekday, rule.Ordinal, rule.MissingDate),
            RecurrenceKind.Yearly => (date.Year - start.Year) % rule.Every == 0 && date.Month == rule.Month && date.Day == ValidDay(rule.Day, date.Year, date.Month, rule.MissingDate),
            _ => false
        };
    }
    private static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    private static int ValidDay(int day, int year, int month, MissingDatePolicy policy) => day <= DateTime.DaysInMonth(year, month) ? day : policy == MissingDatePolicy.LastValid ? DateTime.DaysInMonth(year, month) : -1;
    private static int WeekdayInMonth(int year, int month, DayOfWeek weekday, int ordinal, MissingDatePolicy policy)
    {
        var length = DateTime.DaysInMonth(year, month); var last = length - (((int)new DateOnly(year, month, length).DayOfWeek - (int)weekday + 7) % 7);
        if (ordinal == -1) return last;
        var day = 1 + (((int)weekday - (int)new DateOnly(year, month, 1).DayOfWeek + 7) % 7) + (ordinal - 1) * 7;
        return day <= length ? day : policy == MissingDatePolicy.LastValid ? last : -1;
    }
    public static DateTimeOffset? Resolve(DateTime local, TimeZoneInfo zone, RecurrenceRule rule)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            if (rule.MissingTime == MissingTimePolicy.Skip) return null;
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        }
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local); var offset = rule.RepeatedTime == RepeatedTimePolicy.Earlier ? offsets.Max() : offsets.Min();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
    private sealed class AllDaysCalendar : IWorkCalendar { public string Id => "all"; public string Name => "每天（不排除节假日）"; public bool Allows(DateOnly date) => true; }
    private sealed class WeekdaysCalendar : IWorkCalendar { public string Id => "weekdays"; public string Name => "周一至周五（不含法定节假日数据）"; public bool Allows(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday); }
}
