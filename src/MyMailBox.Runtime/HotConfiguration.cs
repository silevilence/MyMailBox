using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed class HotConfiguration : IAsyncDisposable
{
    private readonly PluginRuntime _runtime;
    private readonly PluginCatalog _catalog;
    private readonly IReadOnlySet<string> _core;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private bool _disposed;
    public string Path { get; }
    public PluginConfiguration Current { get; private set; } = new(1, []);
    public string? Error { get; private set; }
    public event Action? Changed;
    public int AppliedCount { get; private set; }

    public HotConfiguration(PluginRuntime runtime, PluginCatalog catalog, IReadOnlySet<string> core, string path)
    {
        _runtime = runtime; _catalog = catalog; _core = core; Path = path;
        _debounce = new Timer(async _ => { if (!_disposed) await Reload(); }, null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path))
        { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += Schedule; _watcher.Created += Schedule; _watcher.Renamed += Schedule;
        _watcher.EnableRaisingEvents = true;
    }
    private void Schedule(object sender, FileSystemEventArgs e) { if (!_disposed) _debounce.Change(180, Timeout.Infinite); }

    public async Task<bool> Reload()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return false;
            await Apply(ConfigurationFiles.Read(Path));
            Error = null; Changed?.Invoke(); return true;
        }
        catch (Exception e) { Error = e.Message; Changed?.Invoke(); return false; }
        finally { _gate.Release(); }
    }

    public async Task Save(PluginConfiguration config)
    {
        await _gate.WaitAsync();
        var previous = Current;
        try
        {
            await Apply(config);
            try { ConfigurationFiles.Save(Path, config); }
            catch { await Apply(previous); throw; }
            Error = null; Changed?.Invoke();
        }
        catch (Exception e) { Error = e.Message; Changed?.Invoke(); throw; }
        finally { _gate.Release(); }
    }

    private async Task Apply(PluginConfiguration next)
    {
        if (next.SchemaVersion != 1) throw new InvalidDataException("schemaVersion：期望 1");
        if (next.Instances.Select(p => p.Id).Distinct().Count() != next.Instances.Length) throw new InvalidDataException("instances.id：重复");
        foreach (var item in next.Instances)
            if (string.IsNullOrWhiteSpace(item.Id) || item.Options?.Greeting is { Length: > 200 })
                throw new InvalidDataException($"instances[{item.Id}].options.greeting：最多 200 字符；id 不能为空");
        var ordered = _catalog.Order(next, PluginKind.Ordinary, _core);
        var oldOrder = _catalog.Order(Current, PluginKind.Ordinary, _core);
        static string Fingerprint(PluginInstance p) => JsonSerializer.Serialize(p, ConfigurationFiles.Json);
        var before = Current.Instances.ToDictionary(p => p.Id);
        var after = next.Instances.ToDictionary(p => p.Id);
        var affected = before.Keys.Union(after.Keys).Where(id => !before.ContainsKey(id) || !after.ContainsKey(id) || Fingerprint(before[id]) != Fingerprint(after[id])).ToHashSet();
        if (affected.Count == 0) return;
        // Restart dependent instances together with the capability they consume.
        bool added;
        do
        {
            added = false;
            var plugins = before.Values.Concat(after.Values).Where(p => affected.Contains(p.Id)).Select(p => p.Plugin).ToHashSet();
            foreach (var item in oldOrder.Concat(ordered))
                if ((_catalog.Find(item).Manifest.Dependencies ?? []).Any(plugins.Contains)) added |= affected.Add(item.Id);
        } while (added);
        var stopped = new List<PluginInstance>();
        var started = new List<PluginInstance>();
        try
        {
            foreach (var item in oldOrder.Reverse().Where(p => affected.Contains(p.Id)))
            { await _runtime.Stop(item.Id); stopped.Add(item); }
            foreach (var item in ordered.Where(p => affected.Contains(p.Id)))
            { await _runtime.Start(_catalog.Find(item), CoreProfile.Options(item)); started.Add(item); }
        }
        catch (Exception activationError)
        {
            foreach (var item in started.AsEnumerable().Reverse()) await _runtime.Stop(item.Id);
            try
            {
                foreach (var item in stopped.AsEnumerable().Reverse()) await _runtime.Start(_catalog.Find(item), CoreProfile.Options(item));
            }
            catch (Exception recoveryError) { throw new AggregateException("新配置失败且旧状态恢复失败，请重启修复。", activationError, recoveryError); }
            throw new InvalidOperationException("新配置未生效，已保留上次有效状态：" + activationError.Message, activationError);
        }
        Current = next; AppliedCount++;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true; _watcher.Dispose(); await _debounce.DisposeAsync();
        await _gate.WaitAsync(); _gate.Release();
    }
}
