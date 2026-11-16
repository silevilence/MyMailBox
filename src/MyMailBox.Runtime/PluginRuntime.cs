using CordiSharp;
using CordiSharp.Loading;
using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed record PluginDefinition(PluginManifest Manifest, PluginOrigin Origin, string Directory,
    Action<Context, PluginOptions>? Builtin = null);

/// <summary>Project policy around CordiSharp; assembly loading and fiber cleanup remain upstream.</summary>
public sealed class PluginRuntime(Contributions pages) : IAsyncDisposable
{
    private readonly Dictionary<string, RunningPlugin> _running = [];
    public EventHub Events { get; } = new();
    public IReadOnlyCollection<string> Running => _running.Keys;
    public PluginScope Scope(string instance) => _running[instance].Scope;

    public async Task Start(PluginDefinition definition, PluginOptions options)
    {
        if (definition.Manifest.SdkMin > 1 || definition.Manifest.SdkMax < 1)
            throw new InvalidOperationException($"{definition.Manifest.Id}: SDK 范围不兼容，宿主为 1");
        if (_running.ContainsKey(options.InstanceId)) throw new InvalidOperationException("实例已启用：" + options.InstanceId);
        var root = Context.Create();
        var scope = new PluginScope(options.InstanceId, pages, Events);
        root.Provide<IPluginHost>("mailbox.host", scope);
        AssemblyPluginSet? set = null;
        try
        {
            if (definition.Origin == PluginOrigin.Builtin)
                await root.Inject(["mailbox.host"], (ctx, _) => { definition.Builtin!(ctx, options); return null; });
            else
            {
                await root.Plugin(typeof(AssemblyLoaderService));
                var loader = root.Get<AssemblyLoaderService>("loader")!;
                set = loader.LoadAssembly(Path.Combine(definition.Directory, definition.Manifest.Assembly!));
                if (set.IsUnloaded) throw new InvalidOperationException("程序集装入失败：" + definition.Manifest.Assembly);
                await set.LoadPlugin(definition.Manifest.Entry, options);
            }
            _running.Add(options.InstanceId, new(root, scope, set));
        }
        catch
        {
            scope.Dispose();
            if (set is not null) await set.DisposeAsync();
            await root.Fiber.DisposeAsync();
            throw;
        }
    }

    public async Task Stop(string instance)
    {
        if (!_running.Remove(instance, out var running)) return;
        running.Scope.Dispose();
        if (running.Assembly is not null) await running.Assembly.UnloadAsync(verify: true);
        await running.Root.Fiber.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in Running.Reverse().ToArray()) await Stop(id);
    }
    private sealed record RunningPlugin(Context Root, PluginScope Scope, AssemblyPluginSet? Assembly);
}
