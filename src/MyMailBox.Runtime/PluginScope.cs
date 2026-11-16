using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed class PluginScope(string instanceId, Contributions pages, EventHub events, PermissionPolicy permissions, HostOperations operations, Diagnostics? diagnostics) : IPluginHost, IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<string, Func<CancellationToken, Task<string>>> _commands = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private bool _accepting = true;
    private int _active;
    private TaskCompletionSource _idle = Completed();
    public int ActiveOperations { get { lock (_gate) return _active; } }
    public bool Accepting { get { lock (_gate) return _accepting; } }
    private static TaskCompletionSource Completed() { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
    public string InstanceId { get; } = instanceId;
    public IReadOnlyCollection<string> Commands => _commands.Keys;
    public IDisposable Add(Page page) => Own(pages.Add(page with { Id = InstanceId + "/" + page.Id, Execute = (values, action) => Run(_ => page.Execute(values, action)) }));
    public IDisposable AddSelector(Selector selector) => Own(pages.AddSelector(selector));
    public IDisposable AddNative(NativePage page)
    {
        permissions.Require("ui.native");
        return Own(pages.AddNative(page with { Id = InstanceId + "/" + page.Id }));
    }
    public Task<string> Invoke(string capability, string? account = null, CancellationToken cancellation = default)
    {
        try { permissions.Require(capability, account); }
        catch (UnauthorizedAccessException)
        {
            var id = diagnostics?.Record(InstanceId, capability, "denied", "permission-denied");
            throw new UnauthorizedAccessException($"未授予权限：{capability}；关联 ID：{id}");
        }
        diagnostics?.Record(InstanceId, capability, "authorized");
        return operations.Invoke(capability, account, cancellation);
    }
    public IDisposable Command(string id, Func<CancellationToken, Task<string>> execute)
    {
        _commands.Add(id, execute);
        return Own(new Cleanup(() => _commands.Remove(id)));
    }
    public IDisposable Subscribe(Action<string> listener) => Own(events.Subscribe(listener));
    public void Notify(string value) => events.Emit(value);
    public Task<string> Execute(string command, CancellationToken cancellation = default) => Run(token => _commands[command](token), cancellation);
    private async Task<string> Run(Func<CancellationToken, Task<string>> operation, CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            if (!_accepting) throw new InvalidOperationException($"{InstanceId} 正在停止接单/更新，请稍后重试。");
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cancellation.Token);
        try { return await operation(linked.Token); }
        // Do not let plugin exception types/stack traces escape into long-lived host Tasks.
        catch (OperationCanceledException) { throw new OperationCanceledException("Plugin operation canceled", linked.Token); }
        catch (Exception error) { throw new InvalidOperationException(error.Message); }
        finally
        {
            operation = null!;
            lock (_gate) if (--_active == 0) _idle.TrySetResult();
        }
    }
    public async Task Drain(bool cancel, TimeSpan timeout)
    {
        Task idle;
        lock (_gate) { _accepting = false; idle = _idle.Task; }
        if (cancel) _cancellation.Cancel();
        try { await idle.WaitAsync(timeout); }
        catch (TimeoutException e) { throw new InvalidOperationException($"停止操作失败：{InstanceId} 仍有 {ActiveOperations} 项在途任务，尚未释放程序集。", e); }
    }
    private IDisposable Own(IDisposable resource) { _owned.Add(resource); return resource; }
    public void Dispose()
    {
        foreach (var item in _owned.AsEnumerable().Reverse()) item.Dispose();
        _owned.Clear();
        _commands.Clear();
    }
}

public sealed class EventHub
{
    private event Action<string>? Received;
    public int SubscriptionCount => Received?.GetInvocationList().Length ?? 0;
    public IDisposable Subscribe(Action<string> action)
    {
        Received += action;
        return new Cleanup(() => Received -= action);
    }
    public void Emit(string value) => Received?.Invoke(value);
}
