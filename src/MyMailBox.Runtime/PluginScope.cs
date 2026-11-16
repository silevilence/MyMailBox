using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed class PluginScope(string instanceId, Contributions pages, EventHub events) : IPluginHost, IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<string, Func<CancellationToken, Task<string>>> _commands = [];
    public string InstanceId { get; } = instanceId;
    public IReadOnlyCollection<string> Commands => _commands.Keys;
    public IDisposable Add(Page page) => Own(pages.Add(page with { Id = InstanceId + "/" + page.Id }));
    public IDisposable Command(string id, Func<CancellationToken, Task<string>> execute)
    {
        _commands.Add(id, execute);
        return Own(new Cleanup(() => _commands.Remove(id)));
    }
    public IDisposable Subscribe(Action<string> listener) => Own(events.Subscribe(listener));
    public void Notify(string value) => events.Emit(value);
    public Task<string> Execute(string command, CancellationToken cancellation = default) => _commands[command](cancellation);
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
