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
    public IDisposable Add(Page page)
    {
        var execute = Target(page.Execute);
        return Own(pages.Add(page with
        {
            Id = InstanceId + "/" + page.Id, Execute = (values, action) => Run(_ => execute.Value(values, action)),
            Fields = page.Fields.Select(field => field with { Choices = field.Choices?.ToArray() }).ToArray(),
            Actions = page.Actions.ToArray(),
            Reader = page.Reader is { } reader ? new(new ScopedMailStore(this, Target(reader.Store)), new ScopedMimeReader(this, Target(reader.Parser)), reader.Html is { } html ? new ScopedHtml(this, Target(html)) : null, reader.Remote is { } remote ? new ScopedRemote(this, Target(remote)) : null, reader.Attachments is { } attachments ? new ScopedAttachments(this, Target(attachments)) : null) : null,
            Accounts = page.Accounts is { } accounts ? new ScopedAccounts(this, Target(accounts)) : null,
            Protocols = page.Protocols is { } protocols ? new(new ScopedAccounts(this, Target(protocols.Accounts)), new ScopedProtocols(this, Target(protocols.Service))) : null,
            Selections = page.Selections is { } selections ? new ScopedSelections(this, Target(selections)) : null,
            Export = page.Export is { } export ? new(new ScopedMailStore(this, Target(export.Store)), new ScopedSelections(this, Target(export.Selections))) : null,
            LocalMailboxes = page.LocalMailboxes is { } local ? new ScopedLocalMailboxes(this, Target(local)) : null,
            Reminders = page.Reminders is { } reminders ? new(new ScopedReminders(this, Target(reminders.Scheduler)), new ScopedMailStore(this, Target(reminders.Mail))) : null,
            Schedules = page.Schedules is { } schedules ? new ScopedSchedules(this, Target(schedules)) : null,
            Updates = page.Updates is { } updates ? new ScopedUpdates(this, Target(updates)) : null,
            UpdateInstaller = page.UpdateInstaller is { } installer ? new ScopedUpdateInstaller(this, Target(installer)) : null
        }));
    }
    public IDisposable AddSelector(Selector selector)
    {
        var choices = Target(selector.Choices);
        return Own(pages.AddSelector(selector with { Choices = () => choices.Value().ToArray() }));
    }
    public IDisposable AddUpdatePreparation(UpdatePreparation preparation)
    {
        var save = Target(preparation.Save);
        return Own(pages.AddUpdatePreparation(new(InstanceId + "/" + preparation.Id,
            async token => await Run(async t => { await save.Value(t); return true; }, token))));
    }
    public IDisposable AddExporter(IMessageExporter exporter)
    {
        var scoped = new ScopedExporter(this, exporter);
        var registration = pages.AddExporter(scoped);
        return Own(new Cleanup(() => { registration.Dispose(); scoped.Release(); }));
    }
    public IDisposable AddNative(NativePage page)
    {
        permissions.Require("ui.native");
        var create = Target(page.Create);
        var release = Target(page.Release);
        return Own(pages.AddNative(page with { Id = InstanceId + "/" + page.Id,
            Create = () => create.Value(), Release = control => release.Value(control) }));
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

    // Retained host pages must not keep an unloaded plugin's delegates or services alive.
    private ServiceTarget<T> Target<T>(T value) where T : class
    {
        var target = new ServiceTarget<T>(value);
        Own(new Cleanup(target.Release));
        return target;
    }
    private sealed class ServiceTarget<T>(T value) where T : class
    {
        private T? _value = value;
        public T Value => _value ?? throw new InvalidOperationException("插件贡献已卸载。");
        public void Release() => _value = null;
    }

    private sealed class ScopedMailStore(PluginScope scope, ServiceTarget<IMailStore> store) : IMailStore
    {
        public Task<IReadOnlyList<MailFolder>> Folders(CancellationToken cancellation = default) => scope.Run(t => store.Value.Folders(t), cancellation);
        public Task<MailPageResult> List(Guid folderId, int offset, int limit, CancellationToken cancellation = default) => scope.Run(t => store.Value.List(folderId, offset, limit, t), cancellation);
        public Task<Guid> Import(Guid folderId, byte[] raw, ParsedMail metadata, CancellationToken cancellation = default) => scope.Run(t => store.Value.Import(folderId, raw, metadata, t), cancellation);
        public Task<byte[]> Read(Guid messageId, CancellationToken cancellation = default) => scope.Run(t => store.Value.Read(messageId, t), cancellation);
    }

    private sealed class ScopedMimeReader(PluginScope scope, ServiceTarget<IMimeReader> reader) : IMimeReader
    {
        public Task<ParsedMail> Parse(byte[] raw, CancellationToken cancellation = default, string? charsetOverride = null) => scope.Run(t => reader.Value.Parse(raw, t, charsetOverride), cancellation);
    }
    private sealed class ScopedHtml(PluginScope scope, ServiceTarget<IHtmlMailRenderer> renderer) : IHtmlMailRenderer
    {
        public Task<SafeMailDocument> Render(ParsedMail mail, string baseUri, IReadOnlyDictionary<string, MailResource>? loadedRemote = null, CancellationToken cancellation = default) => scope.Run(t => renderer.Value.Render(mail, baseUri, loadedRemote, t), cancellation);
    }
    private sealed class ScopedRemote(PluginScope scope, ServiceTarget<IRemoteContentService> service) : IRemoteContentService
    {
        public Task<RemoteContentPreview> Preview(RemoteContentContext context, CancellationToken cancellation = default) => scope.Run(t => service.Value.Preview(context, t), cancellation);
        public async Task SetRule(RemoteContentContext context, RemoteContentScope kind, bool allow, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.SetRule(context, kind, allow, t); return true; }, cancellation);
        public Task<RemoteLoadResult> Load(RemoteContentContext context, IReadOnlyList<RemoteMailResource> resources, CancellationToken cancellation = default) => scope.Run(t => service.Value.Load(context, resources, t), cancellation);
    }
    private sealed class ScopedAttachments(PluginScope scope, ServiceTarget<IAttachmentService> service) : IAttachmentService
    {
        public Task<SavedAttachment> Save(Guid message, string part, string directory, IProgress<long>? progress = null, CancellationToken cancellation = default) => scope.Run(t => service.Value.Save(message, part, directory, progress, t), cancellation);
        public Task<AttachmentPreview> Preview(Guid message, string part, CancellationToken cancellation = default) => scope.Run(t => service.Value.Preview(message, part, t), cancellation);
    }

    private sealed class ScopedAccounts(PluginScope scope, ServiceTarget<IAccountService> accounts) : IAccountService
    {
        public Task<IReadOnlyList<MailAccount>> List(CancellationToken cancellation = default) => scope.Run(t => accounts.Value.List(t), cancellation);
        public Task<MailAccount> Save(AccountEdit edit, string? newCredential = null, CancellationToken cancellation = default) => scope.Run(t => accounts.Value.Save(edit, newCredential, t), cancellation);
        public async Task Remove(Guid id, CancellationToken cancellation = default) => await scope.Run(async t => { await accounts.Value.Remove(id, t); return true; }, cancellation);
    }

    private sealed partial class ScopedProtocols(PluginScope scope, ServiceTarget<IProtocolMailService> service) : IProtocolMailService
    {
        public Task<ProtocolSettings> Settings(Guid account, CancellationToken cancellation = default) => scope.Run(t => service.Value.Settings(account, t), cancellation);
        public async Task Save(Guid account, ProtocolSettings settings, string? incomingSecret, string? outgoingSecret, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.Save(account, settings, incomingSecret, outgoingSecret, t); return true; }, cancellation);
        public Task<IReadOnlyList<RemoteFolder>> Folders(Guid account, CancellationToken cancellation = default) => scope.Run(t => service.Value.Folders(account, t), cancellation);
        public Task<RemoteMailPage> Summaries(Guid account, string folder, int offset, int limit, CancellationToken cancellation = default) => scope.Run(t => service.Value.Summaries(account, folder, offset, limit, t), cancellation);
        public Task<SendOutcome> Send(Guid account, TextMail message, CancellationToken cancellation = default) => scope.Run(t => service.Value.Send(account, message, t), cancellation);
    }

    private sealed class ScopedSelections(PluginScope scope, ServiceTarget<ISelectionService> selections) : ISelectionService
    {
        public Task<IReadOnlyList<SelectionChoice>> Query(SelectionQuery query, CancellationToken cancellation = default) => scope.Run(t => selections.Value.Query(query, t), cancellation);
        public async Task Save(string slot, SavedSelection selection, CancellationToken cancellation = default) => await scope.Run(async t => { await selections.Value.Save(slot, selection, t); return true; }, cancellation);
        public Task<SavedSelection?> Load(string slot, CancellationToken cancellation = default) => scope.Run(t => selections.Value.Load(slot, t), cancellation);
        public Task<ResolvedSelection> Resolve(SavedSelection selection, CancellationToken cancellation = default) => scope.Run(t => selections.Value.Resolve(selection, t), cancellation);
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
    private sealed class ScopedLocalMailboxes(PluginScope scope, ServiceTarget<ILocalMailboxService> service) : ILocalMailboxService
    {
        public string StorageDescription => service.Value.StorageDescription;
        public Task<IReadOnlyList<LocalMailboxInfo>> List(CancellationToken cancellation = default) => scope.Run(t => service.Value.List(t), cancellation);
        public Task<MailAccount> Create(AccountEdit edit, CancellationToken cancellation = default) => scope.Run(t => service.Value.Create(edit, t), cancellation);
        public async Task SetOpen(Guid id, bool open, string? alias, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.SetOpen(id, open, alias, t); return true; }, cancellation);
        public Task<IReadOnlyList<MailFolder>> Folders(Guid id, CancellationToken cancellation = default) => scope.Run(t => service.Value.Folders(id, t), cancellation);
        public Task<MailFolder> CreateFolder(Guid account, Guid? parent, string name, CancellationToken cancellation = default) => scope.Run(t => service.Value.CreateFolder(account, parent, name, t), cancellation);
        public Task<Guid> Archive(Guid folder, byte[] raw, CancellationToken cancellation = default) => scope.Run(t => service.Value.Archive(folder, raw, t), cancellation);
    }
    private sealed class ScopedReminders(PluginScope scope, ServiceTarget<IReminderService> service) : IReminderService
    {
        public Task<ReminderInfo> Create(ReminderDraft draft, CancellationToken cancellation = default) => scope.Run(t => service.Value.Create(draft, t), cancellation);
        public Task<IReadOnlyList<ReminderInfo>> List(CancellationToken cancellation = default) => scope.Run(t => service.Value.List(t), cancellation);
        public async Task Snooze(Guid id, TimeSpan delay, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.Snooze(id, delay, t); return true; }, cancellation);
        public async Task Dismiss(Guid id, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.Dismiss(id, t); return true; }, cancellation);
        public async Task Cancel(Guid id, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.Cancel(id, t); return true; }, cancellation);
        public Task<int> Tick(CancellationToken cancellation = default) => scope.Run(t => service.Value.Tick(t), cancellation);
    }
    private sealed class ScopedUpdates(PluginScope scope, ServiceTarget<IUpdateService> service) : IUpdateService
    {
        public Task<UpdateSnapshot> Snapshot(CancellationToken cancellation = default) => scope.Run(t => service.Value.Snapshot(t), cancellation);
        public Task<UpdateCheck> Check(bool manual, CancellationToken cancellation = default) => scope.Run(t => service.Value.Check(manual, t), cancellation);
        public async Task Save(UpdateSettings settings, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.Save(settings, t); return true; }, cancellation);
        public async Task Skip(string? version, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.Skip(version, t); return true; }, cancellation);
    }
    private sealed class ScopedUpdateInstaller(PluginScope scope, ServiceTarget<IUpdateInstaller> service) : IUpdateInstaller
    {
        public Task<UpdateInstallStatus> Status(CancellationToken cancellation = default) => scope.Run(t => service.Value.Status(t), cancellation);
        public Task<UpdateInstallStatus> Download(UpdateRelease release, IProgress<UpdateInstallStatus>? progress = null, CancellationToken cancellation = default) => scope.Run(t => service.Value.Download(release, progress, t), cancellation);
        public async Task RequestInstall(CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.RequestInstall(t); return true; }, cancellation);
    }
    private sealed class ScopedSchedules(PluginScope scope, ServiceTarget<IScheduleService> service) : IScheduleService
    {
        public IReadOnlyList<CalendarChoice> Calendars => service.Value.Calendars;
        public Task<IReadOnlyList<ScheduleOccurrence>> Preview(RecurrenceRule rule, CancellationToken cancellation = default) => scope.Run(t => service.Value.Preview(rule, t), cancellation);
        public Task<ScheduleInfo> Create(ScheduleDraft draft, CancellationToken cancellation = default) => scope.Run(t => service.Value.Create(draft, t), cancellation);
        public Task<IReadOnlyList<ScheduleInfo>> List(CancellationToken cancellation = default) => scope.Run(t => service.Value.List(t), cancellation);
        public async Task SetEnabled(Guid id, bool enabled, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.SetEnabled(id, enabled, t); return true; }, cancellation);
        public Task<int> Tick(CancellationToken cancellation = default) => scope.Run(t => service.Value.Tick(t), cancellation);
        public Task<IReadOnlyList<ScheduleRunInfo>> History(Guid id, CancellationToken cancellation = default) => scope.Run(t => service.Value.History(id, t), cancellation);
        public async Task CancelRun(string id, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.CancelRun(id, t); return true; }, cancellation);
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
