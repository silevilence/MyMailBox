namespace MyMailBox.Contracts;

public sealed record Page(string Id, string Title, string Description, IReadOnlyList<Field> Fields,
    IReadOnlyList<PageAction> Actions, Func<IReadOnlyDictionary<string, string>, string, Task<string>> Execute, MailReader? Reader = null, IAccountService? Accounts = null, ISelectionService? Selections = null)
{
    public ExportSource? Export { get; init; }
    public ILocalMailboxService? LocalMailboxes { get; init; }
    public ReminderPage? Reminders { get; init; }
    public IScheduleService? Schedules { get; init; }
    public IUpdateService? Updates { get; init; }
    public IUpdateInstaller? UpdateInstaller { get; init; }
    public ProtocolPage? Protocols { get; init; }
    public IMailSearch? Search { get; init; }
    public OrganizationPage? Organization { get; init; }
    public IUnifiedInbox? UnifiedInbox { get; init; }
    public GraphPage? Graph { get; init; }
    // Preserve the original SDK constructor for already compiled page plugins.
    public Page(string id, string title, string description, IReadOnlyList<Field> fields,
        IReadOnlyList<PageAction> actions, Func<IReadOnlyDictionary<string, string>, string, Task<string>> execute)
        : this(id, title, description, fields, actions, execute, null, null, null) { }
}
public sealed record Field(string Id, string Label, string Value = "", IReadOnlyList<Choice>? Choices = null, bool Multiline = false, string? Selector = null, bool FilePicker = false);
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
    IDisposable AddExporter(IMessageExporter exporter);
    IDisposable AddUpdatePreparation(UpdatePreparation preparation) => throw new NotSupportedException("宿主尚不支持更新保存参与者。");
}

public sealed class Cleanup(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
