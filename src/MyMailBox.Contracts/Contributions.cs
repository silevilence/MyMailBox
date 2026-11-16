namespace MyMailBox.Contracts;

public sealed record Page(string Id, string Title, string Description, IReadOnlyList<Field> Fields,
    IReadOnlyList<PageAction> Actions, Func<IReadOnlyDictionary<string, string>, string, Task<string>> Execute);
public sealed record Field(string Id, string Label, string Value = "", IReadOnlyList<Choice>? Choices = null, bool Multiline = false, string? Selector = null);
public sealed record Choice(string Id, string Label);
public sealed record PageAction(string Id, string Label);
public sealed record Selector(string Key, string Name, Func<IReadOnlyList<Choice>> Choices);
/// <summary>Advanced opt-in UI. The desktop host validates the object is an Avalonia Control.</summary>
public sealed record NativePage(string Id, string Title, Func<object> Create, Action<object> Release);

/// <summary>Host-rendered UI holds no plugin-specific controls or data templates.</summary>
public interface IContributions
{
    IDisposable Add(Page page);
    IDisposable AddSelector(Selector selector);
    IDisposable AddNative(NativePage page);
}

public sealed class Cleanup(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
