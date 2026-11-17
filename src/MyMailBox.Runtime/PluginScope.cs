using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed class PluginScope(string instanceId, Contributions pages, EventHub events, PermissionPolicy permissions, HostOperations operations, Diagnostics? diagnostics) : IPluginHost, IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<string, Func<CancellationToken, Task<string>>> _commands = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private bool _accepting = true;
    private bool _disposed;
    private int _active;
    private TaskCompletionSource _idle = Completed();
    public int ActiveOperations { get { lock (_gate) return _active; } }
    public bool Accepting { get { lock (_gate) return _accepting; } }
    private static TaskCompletionSource Completed() { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
    public string InstanceId { get; } = instanceId;
    public IReadOnlyCollection<string> Commands => _commands.Keys;
    public IDisposable Add(Page page) => Own(pages.Add(page with
    {
        Id = InstanceId + "/" + page.Id, Execute = (values, action) => Run(_ => page.Execute(values, action)),
        Reader = page.Reader is { } reader ? new(new ScopedMailStore(this, reader.Store), new ScopedMimeReader(this, reader.Parser), reader.Html is { } html ? new ScopedHtml(this, html) : null, reader.Remote is { } remote ? new ScopedRemote(this, remote) : null, reader.Attachments is { } attachments ? new ScopedAttachments(this, attachments) : null) : null,
        Accounts = page.Accounts is { } accounts ? new ScopedAccounts(this, accounts) : null,
        Selections = page.Selections is { } selections ? new ScopedSelections(this, selections) : null,
        Export = page.Export is { } export ? new(new ScopedMailStore(this, export.Store), new ScopedSelections(this, export.Selections)) : null,
        LocalMailboxes = page.LocalMailboxes is { } local ? new ScopedLocalMailboxes(this, local) : null,
        Reminders = page.Reminders is { } reminders ? new(new ScopedReminders(this, reminders.Scheduler), new ScopedMailStore(this, reminders.Mail)) : null,
        Schedules = page.Schedules is { } schedules ? new ScopedSchedules(this, schedules) : null
    }));
    public IDisposable AddSelector(Selector selector) => Own(pages.AddSelector(selector));
    public IDisposable AddExporter(IMessageExporter exporter)
    {
        var scoped = new ScopedExporter(this, exporter);
        var registration = pages.AddExporter(scoped);
        return Own(new Cleanup(() => { registration.Dispose(); scoped.Release(); }));
    }
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
        return Run(token => operations.Invoke(capability, account, token), cancellation);
    }
    public IDisposable Command(string id, Func<CancellationToken, Task<string>> execute)
    {
        _commands.Add(id, execute);
        return Own(new Cleanup(() => _commands.Remove(id)));
    }
    public IDisposable Subscribe(Action<string> listener) => Own(events.Subscribe(listener));
    public void Notify(string value) => events.Emit(value);
    public Task<string> Execute(string command, CancellationToken cancellation = default) => Run(token => _commands[command](token), cancellation);
    private async Task<T> Run<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellation = default)
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
    private IDisposable Own(IDisposable resource)
    {
        lock (_gate)
        {
            if (!_disposed) { _owned.Add(resource); return resource; }
        }
        resource.Dispose();
        throw new ObjectDisposedException(InstanceId);
    }
    public void Dispose()
    {
        IDisposable[] resources;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _accepting = false;
            resources = _owned.AsEnumerable().Reverse().ToArray(); _owned.Clear();
        }
        foreach (var item in resources) item.Dispose();
        _commands.Clear();
        _cancellation.Dispose();
    }

    private sealed class ScopedMailStore(PluginScope scope, IMailStore store) : IMailStore
    {
        public Task<IReadOnlyList<MailFolder>> Folders(CancellationToken cancellation = default) => scope.Run(store.Folders, cancellation);
        public Task<MailPageResult> List(Guid folderId, int offset, int limit, CancellationToken cancellation = default) => scope.Run(t => store.List(folderId, offset, limit, t), cancellation);
        public Task<Guid> Import(Guid folderId, byte[] raw, ParsedMail metadata, CancellationToken cancellation = default) => scope.Run(t => store.Import(folderId, raw, metadata, t), cancellation);
        public Task<byte[]> Read(Guid messageId, CancellationToken cancellation = default) => scope.Run(t => store.Read(messageId, t), cancellation);
    }

    private sealed class ScopedMimeReader(PluginScope scope, IMimeReader reader) : IMimeReader
    {
        public Task<ParsedMail> Parse(byte[] raw, CancellationToken cancellation = default, string? charsetOverride = null) => scope.Run(t => reader.Parse(raw, t, charsetOverride), cancellation);
    }
    private sealed class ScopedHtml(PluginScope scope, IHtmlMailRenderer renderer) : IHtmlMailRenderer
    {
        public Task<SafeMailDocument> Render(ParsedMail mail, string baseUri, IReadOnlyDictionary<string, MailResource>? loadedRemote = null, CancellationToken cancellation = default) => scope.Run(t => renderer.Render(mail, baseUri, loadedRemote, t), cancellation);
    }
    private sealed class ScopedRemote(PluginScope scope, IRemoteContentService service) : IRemoteContentService
    {
        public Task<RemoteContentPreview> Preview(RemoteContentContext context, CancellationToken cancellation = default) => scope.Run(t => service.Preview(context, t), cancellation);
        public async Task SetRule(RemoteContentContext context, RemoteContentScope kind, bool allow, CancellationToken cancellation = default) => await scope.Run(async t => { await service.SetRule(context, kind, allow, t); return true; }, cancellation);
        public Task<RemoteLoadResult> Load(RemoteContentContext context, IReadOnlyList<RemoteMailResource> resources, CancellationToken cancellation = default) => scope.Run(t => service.Load(context, resources, t), cancellation);
    }
    private sealed class ScopedAttachments(PluginScope scope, IAttachmentService service) : IAttachmentService
    {
        public Task<SavedAttachment> Save(Guid message, string part, string directory, IProgress<long>? progress = null, CancellationToken cancellation = default) => scope.Run(t => service.Save(message, part, directory, progress, t), cancellation);
        public Task<AttachmentPreview> Preview(Guid message, string part, CancellationToken cancellation = default) => scope.Run(t => service.Preview(message, part, t), cancellation);
    }

    private sealed class ScopedAccounts(PluginScope scope, IAccountService accounts) : IAccountService
    {
        public Task<IReadOnlyList<MailAccount>> List(CancellationToken cancellation = default) => scope.Run(accounts.List, cancellation);
        public Task<MailAccount> Save(AccountEdit edit, string? newCredential = null, CancellationToken cancellation = default) => scope.Run(t => accounts.Save(edit, newCredential, t), cancellation);
        public async Task Remove(Guid id, CancellationToken cancellation = default) => await scope.Run(async t => { await accounts.Remove(id, t); return true; }, cancellation);
    }

    private sealed class ScopedSelections(PluginScope scope, ISelectionService selections) : ISelectionService
    {
        public Task<IReadOnlyList<SelectionChoice>> Query(SelectionQuery query, CancellationToken cancellation = default) => scope.Run(t => selections.Query(query, t), cancellation);
        public async Task Save(string slot, SavedSelection selection, CancellationToken cancellation = default) => await scope.Run(async t => { await selections.Save(slot, selection, t); return true; }, cancellation);
        public Task<SavedSelection?> Load(string slot, CancellationToken cancellation = default) => scope.Run(t => selections.Load(slot, t), cancellation);
        public Task<ResolvedSelection> Resolve(SavedSelection selection, CancellationToken cancellation = default) => scope.Run(t => selections.Resolve(selection, t), cancellation);
    }
    private sealed class ScopedExporter : IMessageExporter
    {
        private readonly PluginScope _scope;
        private IMessageExporter? _exporter;
        public ScopedExporter(PluginScope scope, IMessageExporter exporter)
        { _scope = scope; _exporter = exporter; FormatId = exporter.FormatId; Name = exporter.Name; }
        public string FormatId { get; }
        public string Name { get; }
        public void Release() => _exporter = null;
        public Task<ExportReport> Export(ExportRequest request, IProgress<ExportItemResult>? progress = null, CancellationToken cancellation = default) => _scope.Run(t => (_exporter ?? throw new InvalidOperationException("导出器已卸载。")).Export(request, progress, t), cancellation);
    }
    private sealed class ScopedLocalMailboxes(PluginScope scope, ILocalMailboxService service) : ILocalMailboxService
    {
        public string StorageDescription => service.StorageDescription;
        public Task<IReadOnlyList<LocalMailboxInfo>> List(CancellationToken cancellation = default) => scope.Run(service.List, cancellation);
        public Task<MailAccount> Create(AccountEdit edit, CancellationToken cancellation = default) => scope.Run(t => service.Create(edit, t), cancellation);
        public async Task SetOpen(Guid id, bool open, string? alias, CancellationToken cancellation = default) => await scope.Run(async t => { await service.SetOpen(id, open, alias, t); return true; }, cancellation);
        public Task<IReadOnlyList<MailFolder>> Folders(Guid id, CancellationToken cancellation = default) => scope.Run(t => service.Folders(id, t), cancellation);
        public Task<MailFolder> CreateFolder(Guid account, Guid? parent, string name, CancellationToken cancellation = default) => scope.Run(t => service.CreateFolder(account, parent, name, t), cancellation);
        public Task<Guid> Archive(Guid folder, byte[] raw, CancellationToken cancellation = default) => scope.Run(t => service.Archive(folder, raw, t), cancellation);
    }
    private sealed class ScopedReminders(PluginScope scope, IReminderService service) : IReminderService
    {
        public Task<ReminderInfo> Create(ReminderDraft draft, CancellationToken cancellation = default) => scope.Run(t => service.Create(draft, t), cancellation);
        public Task<IReadOnlyList<ReminderInfo>> List(CancellationToken cancellation = default) => scope.Run(service.List, cancellation);
        public async Task Snooze(Guid id, TimeSpan delay, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Snooze(id, delay, t); return true; }, cancellation);
        public async Task Dismiss(Guid id, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Dismiss(id, t); return true; }, cancellation);
        public async Task Cancel(Guid id, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Cancel(id, t); return true; }, cancellation);
        public Task<int> Tick(CancellationToken cancellation = default) => scope.Run(service.Tick, cancellation);
    }
    private sealed class ScopedSchedules(PluginScope scope, IScheduleService service) : IScheduleService
    {
        public IReadOnlyList<CalendarChoice> Calendars => service.Calendars;
        public Task<IReadOnlyList<ScheduleOccurrence>> Preview(RecurrenceRule rule, CancellationToken cancellation = default) => scope.Run(t => service.Preview(rule, t), cancellation);
        public Task<ScheduleInfo> Create(ScheduleDraft draft, CancellationToken cancellation = default) => scope.Run(t => service.Create(draft, t), cancellation);
        public Task<IReadOnlyList<ScheduleInfo>> List(CancellationToken cancellation = default) => scope.Run(service.List, cancellation);
        public async Task SetEnabled(Guid id, bool enabled, CancellationToken cancellation = default) => await scope.Run(async t => { await service.SetEnabled(id, enabled, t); return true; }, cancellation);
        public Task<int> Tick(CancellationToken cancellation = default) => scope.Run(service.Tick, cancellation);
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
