using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed class Contributions(Action<Action>? dispatch = null) : IContributions
{
    private readonly Dictionary<string, Page> _pages = [];
    private readonly object _gate = new();
    private readonly Dictionary<string, Selector> _selectors = [];
    private readonly Dictionary<string, NativePage> _native = [];
    private readonly Dictionary<string, IMessageExporter> _exporters = [];
    public int ExporterRevision { get; private set; }
    public IReadOnlyList<IMessageExporter> Exporters { get { lock (_gate) return _exporters.Values.ToArray(); } }
    public int SelectorRevision { get; private set; }
    private void Notify() { if (dispatch is null) Changed?.Invoke(); else dispatch(() => Changed?.Invoke()); }
    public event Action? Changed;
    public event Action? NoticeChanged;
    private string _notice = "";
    public string Notice { get => _notice; set { _notice = value; NoticeChanged?.Invoke(); } }
    public IReadOnlyList<Page> Pages { get { lock (_gate) return _pages.Values.ToArray(); } }
    public IReadOnlyList<NativePage> NativePages { get { lock (_gate) return _native.Values.ToArray(); } }
    public IReadOnlyList<Choice> Choices(string key) { lock (_gate) return _selectors.TryGetValue(key, out var provider) ? provider.Choices() : []; }
    public IDisposable AddSelector(Selector selector)
    {
        lock (_gate) { _selectors.Add(selector.Key, selector); SelectorRevision++; }
        Notify();
        return new Cleanup(() => { lock (_gate) { _selectors.Remove(selector.Key); SelectorRevision++; } Notify(); });
    }
    public IDisposable AddNative(NativePage page)
    {
        lock (_gate) _native.Add(page.Id, page);
        Notify();
        return new Cleanup(() => { lock (_gate) _native.Remove(page.Id); Notify(); });
    }
    public IDisposable AddExporter(IMessageExporter exporter)
    {
        lock (_gate) { _exporters.Add(exporter.FormatId, exporter); ExporterRevision++; } Notify();
        return new Cleanup(() => { lock (_gate) { _exporters.Remove(exporter.FormatId); ExporterRevision++; } Notify(); });
    }

    public IDisposable Add(Page page)
    {
        lock (_gate) _pages.Add(page.Id, page);
        Notify();
        return new Cleanup(() =>
        {
            lock (_gate) _pages.Remove(page.Id);
            Notify();
        });
    }
}
