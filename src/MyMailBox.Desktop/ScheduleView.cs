using System.Globalization;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class ScheduleView : UserControl, IDisposable
{
    private readonly IScheduleService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _panel = new() { Spacing = 10 }, _weekly = new() { Spacing = 4 };
    private readonly ListBox _plans = new() { MinHeight = 130, MaxHeight = 220 };
    private readonly TextBox _title = new(), _every = new() { Text = "1" }, _at = new() { Text = "09:00" }, _day = new() { Text = "1" }, _month = new() { Text = "1" };
    private readonly TextBox _start = new() { Text = DateTime.Today.ToString("yyyy-MM-dd HH:mm") }, _end = new(), _windowStart = new(), _windowEnd = new(), _excluded = new() { AcceptsReturn = true, MinHeight = 60 };
    private readonly TextBox _cron = new() { Text = "0 9 * * MON-FRI" };
    private readonly ComboBox _gap = new() { ItemsSource = new[] { "不存在时跳过", "向后调整到首个有效分钟" }, SelectedIndex = 0 };
    private readonly ComboBox _fold = new() { ItemsSource = new[] { "重复时仅较早一次", "重复时仅较晚一次" }, SelectedIndex = 0 };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "固定间隔", "上次完成后间隔", "每天固定时刻", "每周多日", "每月日期", "每月第 N / 最后一个周几", "每年固定日期", "每月月末", "高级 CRON（5 字段）" }, SelectedIndex = 2 };
    private readonly ComboBox _unit = new() { ItemsSource = new[] { "分钟", "小时", "天（固定 24 小时）" }, SelectedIndex = 1 };
    private readonly ComboBox _ordinal = new() { ItemsSource = new[] { "第 1 个", "第 2 个", "第 3 个", "第 4 个", "第 5 个", "最后一个" }, SelectedIndex = 0 };
    private readonly ComboBox _weekday = new() { ItemsSource = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }, SelectedIndex = 1 };
    private readonly ComboBox _missing = new() { ItemsSource = new[] { "不存在时跳过当月/当年", "调整为当月最后有效日期/相同周几" }, SelectedIndex = 0 };
    private readonly ComboBox _zone = new() { HorizontalAlignment = HorizontalAlignment.Stretch }, _calendar = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _silent = new() { Content = "静默，只记录通知" };
    private readonly ComboBox _missed = new() { ItemsSource = new[] { "错过时跳过", "补一次", "限量补跑" }, SelectedIndex = 1 };
    private readonly ComboBox _overlap = new() { ItemsSource = new[] { "有在途执行时跳过", "同一计划排队", "互斥组（同组串行）", "允许有限并发" }, SelectedIndex = 1 };
    private readonly TextBox _catchUp = new() { Text = "3" }, _age = new() { Text = "24" }, _grace = new() { Text = "5" }, _concurrency = new() { Text = "2" }, _queue = new() { Text = "100" }, _timeout = new() { Text = "30" }, _retries = new() { Text = "3" }, _backoff = new() { Text = "30" }, _mutex = new();
    private readonly ListBox _history = new() { MinHeight = 120, MaxHeight = 220 };
    private readonly Dictionary<DayOfWeek, CheckBox> _days = [];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap }, _preview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<(Control Row, Func<bool> Visible)> _conditional = [];
    private string? _fingerprint;
    private bool _busy, _disposed;
    public ScheduleView(IScheduleService service)
    {
        _service = service;
        _panel.Children.Add(new TextBlock { Text = "循环计划", FontSize = 28, FontWeight = FontWeight.SemiBold });
        _panel.Children.Add(new TextBlock { Text = "当前动作为提醒。固定间隔保持开始时刻的周期；完成后间隔从通知投递完成时计时。仅在应用和提醒插件运行期间执行。", TextWrapping = TextWrapping.Wrap });
        _plans.ItemTemplate = new FuncDataTemplate<ScheduleInfo>((p, _) => new TextBlock { Text = p is null ? "" : $"{(p.Enabled ? "启用" : "暂停/结束")} · {p.Title}\n下一次：{(p.NextUtc is { } next ? TimeZoneInfo.ConvertTime(next, TimeZoneInfo.FindSystemTimeZoneById(p.Rule.TimeZoneId)).ToString("yyyy-MM-dd HH:mm zzz") : "无")} · {p.Rule.TimeZoneId}" + (p.Error.Length == 0 ? "" : "\n" + p.Error), TextWrapping = TextWrapping.Wrap });
        Add(_panel, "已保存计划", _plans, "schedule-list");
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Action("暂停所选", "schedule-pause", () => Toggle(false))); actions.Children.Add(Action("重新启用所选", "schedule-resume", () => Toggle(true)));
        actions.Children.Add(Action("预览所选计划", "schedule-preview-saved", async () => ShowPreview(await service.Preview(Selected().Rule, _lifetime.Token), Selected().Rule)));
        actions.Children.Add(Action("刷新", "schedule-refresh", Refresh)); _panel.Children.Add(actions);
        _history.ItemTemplate = new FuncDataTemplate<ScheduleRunInfo>((r, _) => new TextBlock { Text = r is null ? "" : $"{RunState(r.State)} · {r.DueUtc:yyyy-MM-dd HH:mm:ss} UTC · 尝试 {r.Attempts} 次\n重试：{r.RetryUtc:HH:mm:ss} UTC · {r.Error}", TextWrapping = TextWrapping.Wrap });
        var history = new StackPanel { Spacing = 8 }; Add(history, "最近 200 次执行", _history, "schedule-history");
        history.Children.Add(Action("加载所选计划执行记录", "schedule-history-load", async () =>
        {
            var plan = Selected(); _history.ItemsSource = await service.History(plan.Id, _lifetime.Token);
            _status.Text = $"已保存策略：{new[] { "错过跳过", "补一次", "限量补跑" }[(int)plan.Execution.Missed]}；{new[] { "重叠跳过", "排队", "互斥", "允许并发" }[(int)plan.Execution.Overlap]}；超时 {plan.Execution.Timeout.TotalSeconds} 秒；最多重试 {plan.Execution.RetryCount} 次。";
        }));
        history.Children.Add(Action("取消所选执行", "schedule-run-cancel", async () =>
        {
            if (_history.SelectedItem is not ScheduleRunInfo run) throw new InvalidDataException("请选择执行记录。");
            await service.CancelRun(run.Id, _lifetime.Token); _history.ItemsSource = await service.History(run.PlanId, _lifetime.Token); _status.Text = "已请求取消此执行，后续计划仍按规则运行。";
        })); _panel.Children.Add(new Expander { Header = "执行记录与取消", Content = history, HorizontalAlignment = HorizontalAlignment.Stretch });
        Add(_panel, "提醒内容", _title, "schedule-title"); Add(_panel, "循环方式", _kind, "schedule-kind");
        Conditional(Add(_panel, "每 N 个周期（分钟/小时/天/周/月/年，取决于所选方式）", _every, "schedule-every"), () => _kind.SelectedIndex != 8);
        Conditional(Add(_panel, "CRON：分 时 日 月 星期（日期与星期同时限制时取 AND）", _cron, "schedule-cron"), () => _kind.SelectedIndex == 8);
        var cronHelp = new TextBlock { Text = "示例：0 9 * * MON-FRI。支持列表、范围、步长及 L/W/#；周日为 0 或 7。无秒、年份、@别名或 H 随机抖动。", TextWrapping = TextWrapping.Wrap }; _panel.Children.Add(cronHelp); Conditional(cronHelp, () => _kind.SelectedIndex == 8);
        Conditional(Add(_panel, "固定/完成后间隔单位", _unit, "schedule-unit"), () => _kind.SelectedIndex < 2);
        Conditional(Add(_panel, "每日执行时刻（HH:mm）", _at, "schedule-at"), () => _kind.SelectedIndex is >= 2 and < 8);
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var check = new CheckBox { Content = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)day], IsChecked = day == DayOfWeek.Monday };
            AutomationProperties.SetAutomationId(check, "schedule-weekday-" + (int)day); _days.Add(day, check); _weekly.Children.Add(check);
        }
        _panel.Children.Add(_weekly); Conditional(_weekly, () => _kind.SelectedIndex == 3);
        Conditional(Add(_panel, "月份（1–12）", _month, "schedule-month"), () => _kind.SelectedIndex == 6);
        Conditional(Add(_panel, "日期（1–31）", _day, "schedule-day"), () => _kind.SelectedIndex is 4 or 6);
        Conditional(Add(_panel, "第几个", _ordinal, "schedule-ordinal"), () => _kind.SelectedIndex == 5);
        Conditional(Add(_panel, "星期", _weekday, "schedule-weekday"), () => _kind.SelectedIndex == 5);
        Conditional(Add(_panel, "不存在日期的处理", _missing, "schedule-missing-date"), () => _kind.SelectedIndex is 4 or 5 or 6);
        _zone.ItemsSource = TimeZoneInfo.GetSystemTimeZones(); _zone.ItemTemplate = new FuncDataTemplate<TimeZoneInfo>((z, _) => new TextBlock { Text = z?.DisplayName }); _zone.SelectedItem = ((IEnumerable<TimeZoneInfo>)_zone.ItemsSource).FirstOrDefault(z => z.Id == TimeZoneInfo.Local.Id);
        Add(_panel, "计划时区", _zone, "schedule-zone");
        var advanced = new StackPanel { Spacing = 8 };
        Add(advanced, "开始时间 / 周期锚点（yyyy-MM-dd HH:mm）", _start, "schedule-start"); Add(advanced, "结束时间（可空，yyyy-MM-dd HH:mm）", _end, "schedule-end");
        Add(advanced, "每日运行窗口开始（可空，HH:mm）", _windowStart, "schedule-window-start"); Add(advanced, "每日运行窗口结束（可空，HH:mm）", _windowEnd, "schedule-window-end");
        Add(advanced, "排除日期（yyyy-MM-dd，逗号或换行分隔）", _excluded, "schedule-excluded");
        _calendar.ItemsSource = service.Calendars; _calendar.ItemTemplate = new FuncDataTemplate<CalendarChoice>((c, _) => new TextBlock { Text = c?.Name }); _calendar.SelectedIndex = 0;
        Add(advanced, "工作日 / 节假日日历", _calendar, "schedule-calendar");
        Add(advanced, "夏令时缺失时刻", _gap, "schedule-gap"); Add(advanced, "夏令时重复时刻", _fold, "schedule-fold");
        advanced.Children.Add(new TextBlock { Text = "周一至周五日历不代表法定工作日；节假日需由日历插件提供。窗口为同一天内的闭区间。表单与 CRON 共用夏令时策略；固定时长间隔不因夏令时重置。", TextWrapping = TextWrapping.Wrap });
        _panel.Children.Add(new Expander { Header = "开始结束、运行窗口与排除日", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
        var execution = new StackPanel { Spacing = 8 };
        Add(execution, "错过执行时", _missed, "schedule-missed"); Add(execution, "最多补跑次数（1–100）", _catchUp, "schedule-catchup");
        Add(execution, "最多恢复多久以内的执行（小时）", _age, "schedule-max-age"); Add(execution, "迟到容忍（秒，超过后视为错过）", _grace, "schedule-grace");
        Add(execution, "上次尚未完成时", _overlap, "schedule-overlap"); Add(execution, "互斥组名称（留空仅本计划互斥）", _mutex, "schedule-mutex");
        Add(execution, "并发上限（1–8，仅允许并发时生效）", _concurrency, "schedule-concurrency"); Add(execution, "队列上限（1–1000）", _queue, "schedule-queue");
        Add(execution, "执行超时（秒，最多 3600）", _timeout, "schedule-timeout"); Add(execution, "失败重试次数（0–10，不含首次）", _retries, "schedule-retries"); Add(execution, "首次重试等待（秒，之后指数增长，最多 1 小时）", _backoff, "schedule-backoff");
        execution.Children.Add(new TextBlock { Text = "恢复只处理配置窗口内的执行，限量补跑从最早未运行时刻开始，剩余过期时刻跳过。队列、领取、超时和退避持久保存；进程退出后等待下次启动恢复，不会记为已执行。", TextWrapping = TextWrapping.Wrap });
        _panel.Children.Add(new Expander { Header = "错过执行、重叠与重试", Content = execution, HorizontalAlignment = HorizontalAlignment.Stretch });
        _panel.Children.Add(_silent); _panel.Children.Add(Action("预览未来 10 次", "schedule-preview", Preview));
        AutomationProperties.SetAutomationId(_preview, "schedule-preview-result"); _panel.Children.Add(_preview);
        _panel.Children.Add(Action("保存新计划", "schedule-create", async () =>
        {
            var rule = Rule(); if (_fingerprint != JsonSerializer.Serialize(rule)) throw new InvalidDataException("请先预览当前规则，再保存。");
            await service.Create(new(_title.Text ?? "", rule, _silent.IsChecked == true ? ReminderNotificationMode.ListOnly : ReminderNotificationMode.Banner) { Execution = Execution() }, _lifetime.Token);
            await Refresh(); _status.Text = "循环计划已保存，周期锚点不会因重启重置。";
        }));
        AutomationProperties.SetAutomationId(_status, "schedule-status");
        var layout = new DockPanel(); DockPanel.SetDock(_status, Dock.Bottom); layout.Children.Add(_status); layout.Children.Add(new ScrollViewer { Content = _panel }); Content = layout;
        _kind.SelectionChanged += (_, _) => { foreach (var item in _conditional) item.Row.IsVisible = item.Visible(); _fingerprint = null; };
        foreach (var item in _conditional) item.Row.IsVisible = item.Visible();
        AttachedToVisualTree += async (_, _) => await Guard(Refresh);
    }
    private static StackPanel Add(StackPanel panel, string label, Control input, string id)
    { var row = new StackPanel { Spacing = 4 }; row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); row.Children.Add(input); panel.Children.Add(row); AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label); return row; }
    private void Conditional(Control row, Func<bool> visible) => _conditional.Add((row, visible));
    private Button Action(string label, string id, Func<Task> run) { var button = new Button { Content = label }; AutomationProperties.SetAutomationId(button, id); button.Click += async (_, _) => await Guard(run); return button; }
    private async Task Guard(Func<Task> run)
    {
        if (_busy || _disposed) return; _busy = true; _panel.IsEnabled = false;
        try { await run(); } catch (OperationCanceledException) { } catch (Exception e) { if (!_disposed) _status.Text = "操作未完成：" + e.Message; }
        finally { _busy = false; _panel.IsEnabled = true; }
    }
    private static int Number(TextBox input, string name) => int.TryParse(input.Text, out var value) ? value : throw new InvalidDataException(name + "需为整数。");
    private static DateTime Date(TextBox input, string name) => DateTime.TryParseExact(input.Text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new InvalidDataException(name + "格式为 yyyy-MM-dd HH:mm。");
    private static TimeOnly Time(TextBox input, string name) => TimeOnly.TryParseExact(input.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : throw new InvalidDataException(name + "格式为 HH:mm。");
    private RecurrenceRule Rule()
    {
        var every = Number(_every, "周期倍数"); var monthEnd = _kind.SelectedIndex == 7;
        var excluded = (_excluded.Text ?? "").Split([',', '，', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new InvalidDataException("排除日期格式为 yyyy-MM-dd。")).Distinct().ToArray();
        return new()
        {
            Kind = _kind.SelectedIndex == 8 ? RecurrenceKind.Cron : monthEnd ? RecurrenceKind.MonthlyDate : (RecurrenceKind)_kind.SelectedIndex, Every = every,
            Cron = _cron.Text ?? "", MissingTime = (MissingTimePolicy)_gap.SelectedIndex, RepeatedTime = (RepeatedTimePolicy)_fold.SelectedIndex,
            Interval = TimeSpan.FromMinutes(every * (_unit.SelectedIndex == 0 ? 1 : _unit.SelectedIndex == 1 ? 60 : 1440)),
            At = Time(_at, "执行时刻"), Day = monthEnd ? 31 : Number(_day, "日期"), Month = Number(_month, "月份"),
            Ordinal = _ordinal.SelectedIndex == 5 ? -1 : _ordinal.SelectedIndex + 1, Weekday = (DayOfWeek)_weekday.SelectedIndex,
            Weekdays = _days.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray(),
            MissingDate = monthEnd || _missing.SelectedIndex == 1 ? MissingDatePolicy.LastValid : MissingDatePolicy.Skip,
            StartLocal = Date(_start, "开始时间"), EndLocal = string.IsNullOrWhiteSpace(_end.Text) ? null : Date(_end, "结束时间"),
            WindowStart = string.IsNullOrWhiteSpace(_windowStart.Text) ? null : Time(_windowStart, "窗口开始"), WindowEnd = string.IsNullOrWhiteSpace(_windowEnd.Text) ? null : Time(_windowEnd, "窗口结束"),
            ExcludedDates = excluded, CalendarId = (_calendar.SelectedItem as CalendarChoice)?.Id ?? throw new InvalidDataException("请选择日历。"),
            TimeZoneId = (_zone.SelectedItem as TimeZoneInfo)?.Id ?? throw new InvalidDataException("请选择时区。")
        };
    }
    private ScheduleExecutionPolicy Execution() => new()
    {
        Missed = (MissedRunPolicy)_missed.SelectedIndex, CatchUpLimit = Number(_catchUp, "补跑次数"), MaximumAge = TimeSpan.FromHours(Number(_age, "恢复窗口")), MisfireGrace = TimeSpan.FromSeconds(Number(_grace, "迟到容忍")),
        Overlap = (ScheduleOverlapPolicy)_overlap.SelectedIndex, MutexKey = _mutex.Text ?? "", MaxConcurrency = Number(_concurrency, "并发上限"), QueueLimit = Number(_queue, "队列上限"),
        Timeout = TimeSpan.FromSeconds(Number(_timeout, "超时")), RetryCount = Number(_retries, "重试次数"), RetryDelay = TimeSpan.FromSeconds(Number(_backoff, "重试等待"))
    };
    private static string RunState(string state) => state switch { "pending" => "排队中", "retry" => "等待重试", "running" => "执行中", "completed" => "已完成", "failed" => "失败", "timedout" => "已超时", "canceled" => "已取消", "cancel_requested" => "正在取消", "skipped" => "已跳过", _ => state };
    private async Task Preview() { var rule = Rule(); var occurrences = await _service.Preview(rule, _lifetime.Token); ShowPreview(occurrences, rule); _fingerprint = occurrences.Count > 0 ? JsonSerializer.Serialize(rule) : null; }
    private void ShowPreview(IReadOnlyList<ScheduleOccurrence> values, RecurrenceRule rule)
    {
        _preview.Text = (rule.Kind == RecurrenceKind.AfterCompletion ? "完成后间隔：以下按每次立即完成估算；实际从完成时刻重新计时。\n" : "") + (values.Count == 0 ? "所选范围没有未来执行。" : string.Join("\n", values.Select(v => $"{v.Local:yyyy-MM-dd HH:mm} · {v.Zone}（{v.Utc:yyyy-MM-dd HH:mm} UTC）")));
    }
    private ScheduleInfo Selected() => _plans.SelectedItem as ScheduleInfo ?? throw new InvalidDataException("请选择已保存计划。");
    private async Task Toggle(bool enabled) { await _service.SetEnabled(Selected().Id, enabled, _lifetime.Token); await Refresh(); _status.Text = enabled ? "已重新启用，将从下一个符合规则的时刻运行。" : "已暂停；规则和周期锚点保留。"; }
    private async Task Refresh() { var id = (_plans.SelectedItem as ScheduleInfo)?.Id; var plans = await _service.List(_lifetime.Token); if (_disposed) return; _plans.ItemsSource = plans; _plans.SelectedItem = plans.FirstOrDefault(p => p.Id == id) ?? plans.FirstOrDefault(); }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
