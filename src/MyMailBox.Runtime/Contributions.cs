using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed class Contributions(Action<Action>? dispatch = null) : IContributions
{
    private readonly Dictionary<string, Page> _pages = [];
    private readonly object _gate = new();
    private readonly Dictionary<string, Selector> _selectors = [];
    private readonly Dictionary<string,ProtocolPreset> _protocolPresets = [];
    private readonly Dictionary<string, IMailboxProvider> _mailboxProviders = new(StringComparer.Ordinal);
    public IReadOnlyList<IMailboxProvider> MailboxProviders { get { lock (_gate) return _mailboxProviders.Values.ToArray(); } }
    public IDisposable AddMailboxProvider(IMailboxProvider provider)
    {
        var type = provider.Type;
        if (string.IsNullOrWhiteSpace(type.Name) || !type.Features.HasFlag(MailboxFeatures.Read))
            throw new InvalidDataException("邮箱提供者须声明名称与读取能力。");
        lock (_gate) _mailboxProviders.Add(type.Id, provider);
        Notify();
        return new Cleanup(() => { lock (_gate) _mailboxProviders.Remove(type.Id); Notify(); });
    }
    private readonly Dictionary<string, MailImportReader> _importReaders = [];
    public IReadOnlyList<MailImportReader> ImportReaders { get { lock (_gate) return _importReaders.Values.ToArray(); } }
    public IDisposable AddImportReader(MailImportReader reader)
    {
        var format = reader.Format;
        if (format.Version < 1 || string.IsNullOrWhiteSpace(format.Name) || format.Parameters.Select(p => p.Id).Distinct().Count() != format.Parameters.Count)
            throw new InvalidDataException("导入器的名称、版本或参数定义无效。");
        lock (_gate) _importReaders.Add(format.Id, reader);
        Notify();
        return new Cleanup(() => { lock (_gate) _importReaders.Remove(format.Id); Notify(); });
    }
    public IReadOnlyList<ProtocolPreset> ProtocolPresets
    {
        get { lock (_gate) return _protocolPresets.Values.OrderBy(p => p.Name).ToArray(); }
    }

    public IDisposable AddProtocolPreset(ProtocolPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Id) || string.IsNullOrWhiteSpace(preset.Name) ||
            preset.IncomingPort is < 1 or > 65535 || preset.OutgoingPort is < 1 or > 65535 ||
            Uri.CheckHostName(preset.IncomingHost) == UriHostNameType.Unknown || Uri.CheckHostName(preset.OutgoingHost) == UriHostNameType.Unknown)
            throw new InvalidDataException("预设缺少有效名称或服务器。");
        lock (_gate) _protocolPresets.Add(preset.Id,preset);
        Notify();
        return new Cleanup(() => { lock (_gate) _protocolPresets.Remove(preset.Id); Notify(); });
    }
    private readonly Dictionary<string, NativePage> _native = [];
    private readonly Dictionary<string, IMessageExporter> _exporters = [];
    private readonly Dictionary<string,AutomationExtension> _automation=[];
    public IReadOnlyList<AutomationExtension> AutomationExtensions {get{lock(_gate)return _automation.Values.ToArray();}}
    public IDisposable AddAutomationExtension(AutomationExtension extension)
    {
        var d=extension.Descriptor;
        if(d.Version<1||string.IsNullOrWhiteSpace(d.Name)||d.Parameters.Any(p=>string.IsNullOrWhiteSpace(p.Id))||d.Parameters.DistinctBy(p=>p.Id).Count()!=d.Parameters.Length)
            throw new InvalidDataException("自动化扩展的版本或参数定义无效。");
        lock(_gate)_automation.Add(d.Id,extension);Notify();
        return new Cleanup(()=>{lock(_gate)_automation.Remove(d.Id);Notify();});
    }
    private readonly Dictionary<string,NotificationPresenter> _presenters=[];
    public IReadOnlyList<NotificationPresenter> Presenters {get{lock(_gate)return _presenters.Values.ToArray();}}
    public IDisposable AddNotificationPresenter(NotificationPresenter p)
    {
        lock(_gate)_presenters.Add(p.Id,p);Notify();
        return new Cleanup(()=>{lock(_gate)_presenters.Remove(p.Id);Notify();});
    }
    public long? NoticeActivity {get;private set;}
    public void ShowActivity(ActivityBatch batch){NoticeActivity=batch.Last;_notice=batch.Text;NoticeChanged?.Invoke();}
    private readonly Dictionary<string,AddressProvider> _addresses=[];
    public IReadOnlyList<AddressProvider> AddressProviders { get { lock(_gate) return _addresses.Values.ToArray(); } }
    public IDisposable AddAddressProvider(AddressProvider provider)
    {
        lock(_gate) _addresses.Add(provider.Id,provider); Notify();
        return new Cleanup(()=>{lock(_gate) _addresses.Remove(provider.Id);Notify();});
    }
    private readonly Dictionary<string,ComposerEditor> _editors=[];
    public IReadOnlyList<ComposerEditor> Editors { get { lock(_gate) return _editors.Values.ToArray(); } }
    public event Func<string,Task>? BeforePluginStop;
    internal async Task PrepareStop(string instance)
    {
        foreach(var handler in BeforePluginStop?.GetInvocationList().Cast<Func<string,Task>>() ?? []) await handler(instance);
    }
    public IDisposable AddEditor(ComposerEditor editor)
    {
        lock(_gate) _editors.Add(editor.Id,editor); Notify();
        return new Cleanup(()=>{lock(_gate) _editors.Remove(editor.Id);Notify();});
    }
    private readonly Dictionary<string, SearchExtension> _searchExtensions = [];
    public IReadOnlyList<SearchExtension> SearchExtensions { get { lock (_gate) return _searchExtensions.Values.ToArray(); } }
    public IDisposable AddSearchExtension(SearchExtension extension)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(extension.Id, @"\Aplugin\.[a-z0-9-]+(?:\.[a-z0-9-]+)+\.v[1-9][0-9]*\z") ||
            !System.Text.RegularExpressions.Regex.IsMatch(extension.Operator, @"\A[a-z][a-z0-9-]*\z"))
            throw new InvalidDataException("搜索字段需要插件命名空间和版本，运算符需要稳定标识。");
        lock (_gate) _searchExtensions.Add(extension.Id, extension);
        Notify(); return new Cleanup(() => { lock (_gate) _searchExtensions.Remove(extension.Id); Notify(); });
    }
    private readonly Dictionary<string, UpdatePreparation> _preparations = [];
    public IReadOnlyList<UpdatePreparation> UpdatePreparations { get { lock (_gate) return _preparations.Values.ToArray(); } }
    public IDisposable AddUpdatePreparation(UpdatePreparation preparation)
    {
        lock (_gate) _preparations.Add(preparation.Id, preparation);
        return new Cleanup(() => { lock (_gate) _preparations.Remove(preparation.Id); });
    }
    public int ExporterRevision { get; private set; }
    public IReadOnlyList<IMessageExporter> Exporters { get { lock (_gate) return _exporters.Values.ToArray(); } }
    public int SelectorRevision { get; private set; }
    private void Notify() { if (dispatch is null) Changed?.Invoke(); else dispatch(() => Changed?.Invoke()); }
    public event Action? Changed;
    public event Action? NoticeChanged;
    private string _notice = "";
    public string Notice { get => _notice; set { NoticeActivity=null;_notice = value; NoticeChanged?.Invoke(); } }
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
