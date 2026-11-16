using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed class Contributions : IContributions
{
    private readonly Dictionary<string, Page> _pages = [];
    private readonly object _gate = new();
    public event Action? Changed;
    public event Action? NoticeChanged;
    private string _notice = "";
    public string Notice { get => _notice; set { _notice = value; NoticeChanged?.Invoke(); } }
    public IReadOnlyList<Page> Pages { get { lock (_gate) return _pages.Values.ToArray(); } }

    public IDisposable Add(Page page)
    {
        lock (_gate) _pages.Add(page.Id, page);
        Changed?.Invoke();
        return new Cleanup(() =>
        {
            lock (_gate) _pages.Remove(page.Id);
            Changed?.Invoke();
        });
    }
}
