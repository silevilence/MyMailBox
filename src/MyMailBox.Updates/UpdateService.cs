using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Updates;

public sealed class UpdateService : IUpdateService, IDisposable
{
    private readonly string _path, _current;
    private readonly Func<string, IUpdateSource> _sources;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1);
    private UpdateSnapshot _state;
    private IUpdateSource _source;
    public UpdateService(string directory, string currentVersion, string repository, Func<string, IUpdateSource> sources, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(directory); _path = Path.Combine(directory, "updates.json"); _current = currentVersion; _sources = sources; _clock = clock ?? TimeProvider.System;
        _state = new(currentVersion, new(repository), new(UpdateCheckState.NotChecked, "尚未检查更新。", DateTimeOffset.MinValue));
        if (File.Exists(_path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<UpdateSnapshot>(File.ReadAllText(_path)) ?? throw new JsonException("空配置。");
                Validate(saved.Settings);
                _state = saved with { CurrentVersion = currentVersion, Last = saved.CurrentVersion == currentVersion ? saved.Last : _state.Last };
            }
            catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException) { throw new InvalidDataException("更新配置损坏，请检查 " + _path, e); }
        }
        _source = _sources(_state.Settings.Repository);
    }
    private static void Validate(UpdateSettings settings)
    {
        GitHubReleaseSource.ValidateRepository(settings.Repository);
        if (!Enum.IsDefined(settings.Channel) || settings.IntervalHours is < 1 or > 168) throw new ArgumentException("检查间隔须为 1–168 小时。");
        if (settings.SkippedVersion is not null && ReleaseVersion.Parse(settings.SkippedVersion) is null) throw new ArgumentException("跳过版本无效。");
    }
    public async Task<UpdateSnapshot> Snapshot(CancellationToken cancellation = default)
    { await _gate.WaitAsync(cancellation); try { return _state; } finally { _gate.Release(); } }
    public async Task Save(UpdateSettings settings, CancellationToken cancellation = default)
    {
        Validate(settings); await _gate.WaitAsync(cancellation);
        try
        {
            var source = _sources(settings.Repository);
            var changed = source.Id != _source.Id || settings.Channel != _state.Settings.Channel;
            _state = _state with { Settings = settings, Last = changed ? new(UpdateCheckState.NotChecked, "更新源或通道已改变，请重新检查。", DateTimeOffset.MinValue) : _state.Last };
            _source = source; Persist();
        }
        finally { _gate.Release(); }
    }
    public async Task<UpdateCheck> Check(bool manual, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var now = _clock.GetUtcNow(); var last = _state.Last;
            var delay = manual ? TimeSpan.FromSeconds(30) : TimeSpan.FromHours(_state.Settings.IntervalHours);
            if ((!manual && !_state.Settings.Background) || last.RetryAfterUtc > now || now >= last.CheckedUtc && now - last.CheckedUtc < delay)
                return last with { Cached = true, State = manual && last.State == UpdateCheckState.Skipped ? UpdateCheckState.Available : last.State };
            var result = await _source.Check(_current, _state.Settings.Channel, cancellation);
            if (!manual && result.Release is { } release && release.Version == _state.Settings.SkippedVersion)
                result = result with { State = UpdateCheckState.Skipped, Message = "已跳过 V" + release.Version + "；可手动检查重新查看。" };
            _state = _state with { Last = result }; Persist(); return result;
        }
        finally { _gate.Release(); }
    }
    public async Task Skip(string? version, CancellationToken cancellation = default)
    {
        if (version is not null) version = ReleaseVersion.Parse(version)?.ToFullString() ?? throw new ArgumentException("版本无效。");
        await _gate.WaitAsync(cancellation);
        try { _state = _state with { Settings = _state.Settings with { SkippedVersion = version } }; Persist(); }
        finally { _gate.Release(); }
    }
    private void Persist()
    {
        var temp = _path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, _path, true);
    }
    public void Dispose() => _gate.Dispose();
}

public sealed class UpdateRunner : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public UpdateRunner(IUpdateService service, Action<string>? notify = null)
    {
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                try { var result = await service.Check(false, _stop.Token); if (!result.Cached && result.State == UpdateCheckState.Available) notify?.Invoke(result.Message + " 可到“软件更新”查看。"); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) { notify?.Invoke("后台更新检查未完成：" + e.Message); }
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        });
    }
    public void Dispose() { _stop.Cancel(); try { _loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } _stop.Dispose(); }
}
