using CordiSharp;
using CordiSharp.Loading;
using CordiSharp.Registry;
using MyMailBox.Contracts;
using System.Collections.Concurrent;

namespace MyMailBox.Runtime;

public sealed record PluginDefinition(PluginManifest Manifest, PluginOrigin Origin, string Directory,
    Action<Context, PluginOptions>? Builtin = null);

/// <summary>Project policy around CordiSharp; assembly loading and fiber cleanup remain upstream.</summary>
public sealed class PluginRuntime(Contributions pages, HostOperations? operations = null, Diagnostics? diagnostics = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RunningPlugin> _running = new();
    private readonly ConcurrentDictionary<string, string> _states = new();
    private long _sequence;
    public IReadOnlyDictionary<string, string> States => new Dictionary<string, string>(_states);
    public EventHub Events { get; } = new();
    public IReadOnlyCollection<string> Running => _running.Keys.ToArray();
    public PluginScope Scope(string instance) => _running[instance].Scope;

    public async Task Start(PluginDefinition definition, PluginOptions options)
    {
        if (definition.Manifest.SdkMin > 1 || definition.Manifest.SdkMax < 1)
            throw new InvalidOperationException($"{definition.Manifest.Id}: SDK 范围不兼容，宿主为 1");
        if (_running.ContainsKey(options.InstanceId)) throw new InvalidOperationException("实例已启用：" + options.InstanceId);
        var root = Context.Create();
        _states[options.InstanceId] = "正在启用";
        var scope = new PluginScope(options.InstanceId, pages, Events, new(definition.Manifest.Permissions ?? [], options.Grants), operations ?? new(), diagnostics);
        root.Provide<IPluginHost>("mailbox.host", scope);
        AssemblyPluginSet? set = null;
        PluginHandle? handle = null;
        try
        {
            if (definition.Origin == PluginOrigin.Builtin)
            {
                handle = root.Inject(["mailbox.host"], (ctx, _) => { definition.Builtin!(ctx, options); return null; });
                await handle;
            }
            else
            {
                handle = root.Plugin(typeof(AssemblyLoaderService));
                await handle;
                var loader = root.Get<AssemblyLoaderService>("loader")!;
                set = loader.LoadAssembly(Path.Combine(definition.Directory, definition.Manifest.Assembly!));
                if (set.IsUnloaded) throw new InvalidOperationException("程序集装入失败：" + definition.Manifest.Assembly);
                await set.LoadPlugin(definition.Manifest.Entry, options);
            }
            if (!_running.TryAdd(options.InstanceId, new(root, scope, set, handle, definition.Manifest, Interlocked.Increment(ref _sequence)))) throw new InvalidOperationException("实例重复启用");
            _states[options.InstanceId] = "已启用 " + definition.Manifest.Version;
            diagnostics?.Record(options.InstanceId, "activate", "success");
        }
        catch (Exception error)
        {
            var correlation = diagnostics?.Record(options.InstanceId, "activate", "failed", "activation-failed");
            _states[options.InstanceId] = "启用失败：" + error.Message + (correlation is null ? "" : "；关联 ID：" + correlation);
            scope.Dispose();
            if (set is not null) await set.DisposeAsync();
            if (handle is not null) await handle.DisposeAsync();
            await root.Fiber.DisposeAsync();
            throw new InvalidOperationException(_states[options.InstanceId]);
        }
    }

    public async Task Stop(string instance, bool cancel = false, TimeSpan? timeout = null)
    {
        if (!_running.TryGetValue(instance, out var running)) return;
        if (_states[instance].StartsWith("资源/程序集释放失败")) throw new InvalidOperationException(_states[instance]);
        _states[instance] = "正在排空在途操作";
        try { await running.Scope.Drain(cancel, timeout ?? TimeSpan.FromSeconds(5)); }
        catch (Exception e) { _states[instance] = e.Message; diagnostics?.Record(instance, "drain", "failed", "drain-timeout"); throw; }
        try
        {
            running.Scope.Dispose();
            if (running.Assembly is not null) await running.Assembly.UnloadAsync(verify: true);
            // Dispose the child handle first: CordiSharp 0.2.0 root disposal restarts its fiber,
            // whose notification walk otherwise races the pump removing child fibers.
            await running.Handle.DisposeAsync();
            await running.Root.Fiber.DisposeAsync();
            _running.TryRemove(instance, out _);
            _states[instance] = "已停用并释放";
            diagnostics?.Record(instance, "unload", "success");
        }
        catch (Exception e) { _states[instance] = "资源/程序集释放失败，需要重启：" + e.Message; diagnostics?.Record(instance, "unload", "failed", "assembly-retained"); throw; }
    }
    public bool Uses(string plugin, string version) => _running.Values.Any(p => p.Manifest.Id == plugin && p.Manifest.Version == version);

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _running.Values.OrderByDescending(p => p.Sequence).Select(p => p.Scope.InstanceId).ToArray())
        {
            var running = _running[id];
            await running.Scope.Drain(false, TimeSpan.FromSeconds(5));
            running.Scope.Dispose();
            if (running.Assembly is not null) await running.Assembly.DisposeAsync();
            // Dispose the child handle first: CordiSharp 0.2.0 root disposal restarts its fiber,
            // whose notification walk otherwise races the pump removing child fibers.
            await running.Handle.DisposeAsync();
            await running.Root.Fiber.DisposeAsync();
            _running.TryRemove(id, out _);
        }
    }
    private sealed record RunningPlugin(Context Root, PluginScope Scope, AssemblyPluginSet? Assembly, PluginHandle Handle, PluginManifest Manifest, long Sequence);
}
