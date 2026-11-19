using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed partial class PluginScope(string instanceId, Contributions pages, EventHub events, PermissionPolicy permissions, HostOperations operations, Diagnostics? diagnostics, bool builtin = false) : IPluginHost, IDisposable
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
            Protocols = page.Protocols is { } protocols ? new(new ScopedAccounts(this, Target(protocols.Accounts)), new ScopedProtocols(this, Target(protocols.Service)))
                { OfflineMarks = protocols.OfflineMarks is { } marks ? new ScopedOfflineMarks(this,Target(marks)) : null,
                  Outbox = protocols.Outbox is { } outbox ? new ScopedOutbox(this, Target(outbox)) : null,
                  ImapSync = protocols.ImapSync is { } sync ? new ScopedImapSync(this, Target(sync)) : null } : null,
            Composer = page.Composer is { } compose ? new(new ScopedAccounts(this, Target(compose.Accounts)),new ScopedDrafts(this,Target(compose.Drafts)),new ScopedOutbox(this,Target(compose.Outbox))) { Graph = compose.Graph is { } composeGraph ? new ScopedGraphWrites(this, Target(composeGraph)) : null } : null,
            Activity=page.Activity is {} activity?new(new ScopedActivity(this,Target(activity.Center)),new ScopedAccounts(this,Target(activity.Accounts))):null,
            Automation=page.Automation is {} auto ? new ScopedAutomation(this,Target(auto)) : null,
            Templates = page.Templates is { } templates ? new ScopedTemplates(this,Target(templates)) : null,
            Rules = page.Rules is { } rules ? new ScopedRules(this,Target(rules)) : null,
            Search = page.Search is { } search ? new ScopedSearch(this,Target(search)) : null,
            Organization = page.Organization is { } org ? new(new ScopedAccounts(this, Target(org.Accounts)), new ScopedOrganizer(this, Target(org.Organizer)))
                { Transfers = org.Transfers is { } transfers ? new ScopedTransfers(this,Target(transfers)) : null,
                  Actions = org.Actions is { } actions ? new ScopedActions(this,Target(actions)) : null } : null,
            UnifiedInbox = page.UnifiedInbox is { } inbox ? new ScopedUnifiedInbox(this, Target(inbox)) : null,
            Graph = page.Graph is { } graph ? new(new ScopedAccounts(this, Target(graph.Accounts)), new ScopedGraph(this, Target(graph.Service)))
                { Writes = graph.Writes is { } writes ? new ScopedGraphWrites(this, Target(writes)) : null, Sync = graph.Sync is { } graphSync ? new ScopedGraphSync(this, Target(graphSync)) : null } : null,
            Selections = page.Selections is { } selections ? new ScopedSelections(this, Target(selections)) : null,
            Export = page.Export is { } export ? new(new ScopedMailStore(this, Target(export.Store)), new ScopedSelections(this, Target(export.Selections))) : null,
            LocalMailboxes = page.LocalMailboxes is { } local ? new ScopedLocalMailboxes(this, Target(local)) : null,
            PstImport = page.PstImport is { } pst ? new ScopedPstImport(this, Target(pst)) : null,
            OutlookAccounts = page.OutlookAccounts is { } outlook ? new ScopedOutlookAccounts(this, Target(outlook)) : null,
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
    public IReadOnlyList<ProtocolPreset> ProtocolPresets => pages.ProtocolPresets;
    public IReadOnlyList<MailImportReader> ImportReaders => pages.ImportReaders;
    public IDisposable AddImportReader(MailImportReader reader)
    {
        var read = Target(reader.Read);
        var format = reader.Format with { Id = InstanceId + "/" + reader.Format.Id, Parameters = reader.Format.Parameters.ToArray() };
        return Own(pages.AddImportReader(new(format, (request, receive, token) => Run(async t =>
        {
            permissions.Require("files.read");
            if (!request.Preview) permissions.Require("mail.write", request.Target?.ToString());
            await read.Value(request, (item, _) => receive(item, t), t);
            return true;
        }, token))));
    }
    public IDisposable AddProtocolPreset(ProtocolPreset preset) => Own(pages.AddProtocolPreset(preset with { Id = InstanceId + "/" + preset.Id }));

    public IDisposable AddAutomationExtension(AutomationExtension extension)
    {
        var d=extension.Descriptor;var evaluate=Target(extension.Evaluate);var migrate=Target(extension.Migrate);
        string capability=d.Part switch{AutomationPart.Condition=>"automation.condition",AutomationPart.Action=>"automation.action",AutomationPart.Schedule=>"automation.schedule",_=>throw new InvalidDataException("未知扩展类型。")};
        if(d.Capability!=capability)throw new InvalidDataException("扩展类型与声明能力不匹配。");
        return Own(pages.AddAutomationExtension(new(d with{Id=InstanceId+"/"+d.Id},
            (input,parameters,token)=>Run(async t=>
            {
                permissions.Require(capability,input.Account.ToString());
                var answer=await evaluate.Value(input,parameters,t);
                if(d.Part==AutomationPart.Action && answer.Effect is not null)permissions.Require("mail.write",input.Account.ToString());
                return answer;
            },token),(v,p,token)=>Run(t=>migrate.Value(v,p,t),token))));
    }
    public IDisposable AddNotificationPresenter(NotificationPresenter p)
    {
        var show=Target(p.Show);
        return Own(pages.AddNotificationPresenter(p with{Id=InstanceId+"/"+p.Id,Show=async(batch,token)=>
        {permissions.Require("notification.show");await Run(async t=>{await show.Value(batch,t);return true;},token);}}));
    }
    public IDisposable AddAddressProvider(AddressProvider provider)
    {
        var suggest=Target(provider.Suggest);
        return Own(pages.AddAddressProvider(provider with {Id=InstanceId+"/"+provider.Id,Suggest=(a,text,token)=>
        {
            permissions.Require("contacts.suggest",a.ToString());return Run(t=>suggest.Value(a,text,t),token);
        }}));
    }
    public IDisposable AddEditor(ComposerEditor editor)
    {
        var open=Target(editor.Open);var spell=Target(editor.Spell);var signature=editor.Signature is {} sig ? Target(sig) : null;
        return Own(pages.AddEditor(editor with {Id=InstanceId+"/"+editor.Id,Templates=editor.Templates.ToArray(),
            Signature=signature is null ? null : account=>{permissions.Require("mail.compose");return Run(_=>Task.FromResult(signature.Value(account))).GetAwaiter().GetResult();},
            Open=body=>{permissions.Require("mail.compose");return Run(_=>Task.FromResult(open.Value(body))).GetAwaiter().GetResult();},
            Spell=(text,token)=>{permissions.Require("mail.compose");return Run(t=>spell.Value(text,t),token);}}));
    }
    public IDisposable AddSearchExtension(SearchExtension extension)
    {
        var plan = Target(extension.CandidateContains); var extract = Target(extension.Extract); var match = Target(extension.Match);
        bool CanRead(Guid account)
        {
            try { permissions.Require("mail.read",account.ToString()); return true; }
            catch (UnauthorizedAccessException) { return false; }
        }
        SearchCandidate[] Allowed(IReadOnlyList<SearchCandidate> items) => items.Where(item=>CanRead(item.Account)).ToArray();
        return Own(pages.AddSearchExtension(extension with
        {
            CanRead = CanRead, CandidateContains = value => Run(_ => Task.FromResult(plan.Value(value))).GetAwaiter().GetResult(),
            Extract = (items,token) => Run(t => extract.Value(Allowed(items),t),token),
            Match = (value,items,token) => Run(t => match.Value(value,Allowed(items),t),token)
        }));
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
        public IReadOnlyList<MailboxType> ProviderTypes => accounts.Value.ProviderTypes;
        public Task<IReadOnlyList<MailAccount>> List(CancellationToken cancellation = default) => scope.Run(t => accounts.Value.List(t), cancellation);
        public Task<MailAccount> Save(AccountEdit edit, string? newCredential = null, CancellationToken cancellation = default) => scope.Run(t => accounts.Value.Save(edit, newCredential, t), cancellation);
        public async Task Remove(Guid id, CancellationToken cancellation = default) => await scope.Run(async t => { await accounts.Value.Remove(id, t); return true; }, cancellation);
    }

    private sealed class ScopedGraphWrites(PluginScope scope, ServiceTarget<IGraphWrites> service) : IGraphWrites
    {
        public async Task Enable(Guid account, CancellationToken token) => await scope.Run(async t => { await service.Value.Enable(account, t); return true; }, token);
        public async Task MarkRead(Guid account, string message, bool read, CancellationToken token) => await scope.Run(async t => { await service.Value.MarkRead(account, message, read, t); return true; }, token);
        public Task<string> Move(Guid account, string message, string destination, CancellationToken token) => scope.Run(t => service.Value.Move(account, message, destination, t), token);
        public Task<string> SaveDraft(MailDraft draft, CancellationToken token) => scope.Run(t => service.Value.SaveDraft(draft, t), token);
    }
    private sealed class ScopedGraphSync(PluginScope scope, ServiceTarget<IGraphFolderSync> service) : IGraphFolderSync
    {
        public Task<GraphSyncScope> Sync(Guid account, GraphFolder folder, CancellationToken token) => scope.Run(t => service.Value.Sync(account, folder, t), token);
        public Task<IReadOnlyList<GraphSyncScope>> Scopes(Guid account, CancellationToken token) => scope.Run(t => service.Value.Scopes(account, t), token);
        public Task<GraphOfflinePage> Cached(Guid account, string folder, int offset, CancellationToken token) => scope.Run(t => service.Value.Cached(account, folder, offset, t), token);
    }
    private sealed class ScopedGraph(PluginScope scope, ServiceTarget<IGraphMailService> service) : IGraphMailService
    {
        public Task<GraphSettings> Settings(Guid account, CancellationToken cancellation = default) => scope.Run(t => service.Value.Settings(account, t), cancellation);
        public async Task SignIn(Guid account, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.SignIn(account, t); return true; }, cancellation);
        public async Task Disconnect(Guid account, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.Disconnect(account, t); return true; }, cancellation);
        public Task<GraphFolders> Folders(Guid account, string? parent = null, string? next = null, CancellationToken cancellation = default) => scope.Run(t => service.Value.Folders(account, parent, next, t), cancellation);
        public Task<GraphMessages> Messages(Guid account, string folder, string? next = null, CancellationToken cancellation = default) => scope.Run(t => service.Value.Messages(account, folder, next, t), cancellation);
        public Task<Guid> Download(Guid account, string message, CancellationToken cancellation = default) => scope.Run(t => service.Value.Download(account, message, t), cancellation);
    }
    private sealed class ScopedTransfers(PluginScope scope, ServiceTarget<IMailTransferService> service) : IMailTransferService
    {
        public Task<IReadOnlyList<MailFolder>> Destinations(Guid sourceAccount,CancellationToken token=default) =>
            scope.Run(t=>service.Value.Destinations(sourceAccount,t),token);
        public Task<MailTransfer> Plan(TransferRequest request,CancellationToken token=default) =>
            scope.Run(t=>service.Value.Plan(request,t),token);
        public Task<MailTransfer> Continue(Guid operation,IProgress<MailTransfer>? progress=null,CancellationToken token=default) =>
            scope.Run(t=>service.Value.Continue(operation,progress,t),token);
        public Task<IReadOnlyList<TransferEvent>> History(Guid operation, CancellationToken token = default) =>
            scope.Run(t=>service.Value.History(operation,t),token);
        public async Task Cancel(Guid operation, CancellationToken token = default) =>
            await scope.Run(async t=>{ await service.Value.Cancel(operation,t); return true; },token);
        public Task<IReadOnlyList<MailTransfer>> Recent(Guid sourceAccount,int offset,int limit,CancellationToken token=default) =>
            scope.Run(t=>service.Value.Recent(sourceAccount,offset,limit,t),token);
    }

    private sealed class ScopedDrafts(PluginScope scope,ServiceTarget<IMailDrafts> service) : IMailDrafts
    {
        public Guid SourceAccount(string message)=>scope.Run(_=>Task.FromResult(service.Value.SourceAccount(message))).GetAwaiter().GetResult();
        public Task<MailDraft> Respond(ReplyIntent intent,Guid account,CancellationToken token=default)=>scope.Run(t=>service.Value.Respond(intent,account,t),token);
        public RecipientFields Normalize(RecipientFields value)=>scope.Run(_=>Task.FromResult(service.Value.Normalize(value))).GetAwaiter().GetResult();
        public Task<IReadOnlyList<AddressChoice>> Suggest(Guid a,string text,CancellationToken token=default)=>scope.Run(t=>service.Value.Suggest(a,text,t),token);
        public void Contact(Guid a,AddressChoice choice)=>scope.Run(_=>{service.Value.Contact(a,choice);return Task.FromResult(true);}).GetAwaiter().GetResult();
        public void Forget(Guid a,string? addr)=>scope.Run(_=>{service.Value.Forget(a,addr);return Task.FromResult(true);}).GetAwaiter().GetResult();
        public SendingProfile Profile(Guid account)=>scope.Run(_=>Task.FromResult(service.Value.Profile(account))).GetAwaiter().GetResult();
        public void Configure(Guid account,SendingProfile profile)=>scope.Run(_=>{service.Value.Configure(account,profile);return Task.FromResult(true);}).GetAwaiter().GetResult();
        public Task<ComposerPart> AddPart(Guid draft,Guid account,string name,Stream data,bool inline,CancellationToken token=default)=>scope.Run(t=>service.Value.AddPart(draft,account,name,data,inline,t),token);
        public byte[] PartData(Guid account,Guid id)=>scope.Run(_=>Task.FromResult(service.Value.PartData(account,id))).GetAwaiter().GetResult();
        public IReadOnlyList<MailDraft> List(Guid account)=>scope.Run(_=>Task.FromResult(service.Value.List(account))).GetAwaiter().GetResult();
        public MailDraft Save(MailDraft draft)=>scope.Run(_=>Task.FromResult(service.Value.Save(draft))).GetAwaiter().GetResult();
        public void Submitted(Guid draft,int version)=>scope.Run(_=>{service.Value.Submitted(draft,version);return Task.FromResult(true);}).GetAwaiter().GetResult();
    }
    private sealed class ScopedActivity(PluginScope scope,ServiceTarget<IActivityCenter> service):IActivityCenter
    {
        public Task<IReadOnlyList<ActivityEntry>> Recent(int offset=0,int limit=50,CancellationToken token=default)=>scope.Run(t=>service.Value.Recent(offset,limit,t),token);
        public Task<NoticePolicy> Policy(CancellationToken token=default)=>scope.Run(t=>service.Value.Policy(t),token);
        public async Task Configure(NoticePolicy settings,CancellationToken token=default)=>await scope.Run(async t=>{await service.Value.Configure(settings,t);return true;},token);
        public Task<bool> Enabled(string kind,Guid? account,CancellationToken token=default)=>scope.Run(t=>service.Value.Enabled(kind,account,t),token);
        public async Task SetEnabled(string kind,Guid? account,bool enabled,CancellationToken token=default)=>await scope.Run(async t=>{await service.Value.SetEnabled(kind,account,enabled,t);return true;},token);
        public Task<ActivityLink> Resolve(long entry,CancellationToken token=default)=>scope.Run(t=>service.Value.Resolve(entry,t),token);
    }
    private sealed class ScopedAutomation(PluginScope scope,ServiceTarget<IMailAutomation> service):IMailAutomation
    {
        public Task<IReadOnlyList<AutomationPlan>> Plans(CancellationToken token=default)=>scope.Run(t=>service.Value.Plans(t),token);
        public Task<AutomationPlan> Save(AutomationPlan plan,CancellationToken token=default)=>scope.Run(t=>service.Value.Save(plan,t),token);
        public Task<TargetSnapshot> Select(AutomationPlan plan,DateTimeOffset? at=null,CancellationToken token=default)=>scope.Run(t=>service.Value.Select(plan,at,t),token);
        public Task<string?> Recheck(AutomationPlan plan,TargetSnapshot snapshot,TargetMail mail,CancellationToken token=default)=>scope.Run(t=>service.Value.Recheck(plan,snapshot,mail,t),token);
        public IReadOnlyList<AutomationDescriptor> Extensions=>service.Value.Extensions;
        public Task SystemEvent(string kind,CancellationToken token=default)=>scope.Run(async t=>{await service.Value.SystemEvent(kind,t);return true;},token);
        public Task<ActionPreview> PreviewActions(AutomationPlan plan,CancellationToken token=default)=>scope.Run(t=>service.Value.PreviewActions(plan,t),token);
        public Task<IReadOnlyList<TaskStepLog>> Steps(Guid run,CancellationToken token=default)=>scope.Run(t=>service.Value.Steps(run,t),token);
        public Task<AutomationRun> Start(Guid plan,string cause,CancellationToken token=default)=>scope.Run(t=>service.Value.Start(plan,cause,t),token);
        public Task<IReadOnlyList<AutomationRun>> Runs(Guid plan,CancellationToken token=default)=>scope.Run(t=>service.Value.Runs(plan,t),token);
        public Task<AutomationRun> Advance(Guid run,CancellationToken token=default)=>scope.Run(t=>service.Value.Advance(run,t),token);
        public Task<AutomationAudit> Audit(Guid run,int offset=0,int limit=50,CancellationToken token=default)=>scope.Run(t=>service.Value.Audit(run,offset,limit,t),token);
        public Task<string> ExportAudit(Guid run,CancellationToken token=default)=>scope.Run(t=>service.Value.ExportAudit(run,t),token);
        public async Task Cancel(Guid run,CancellationToken token=default)=>await scope.Run(async t=>{await service.Value.Cancel(run,t);return true;},token);
        public async Task Control(Guid run,bool resume,CancellationToken token=default)=>await scope.Run(async t=>{await service.Value.Control(run,resume,t);return true;},token);
        public Task<ConditionReport> Inspect(AutomationPlan plan,bool observe=false,CancellationToken token=default)=>scope.Run(t=>service.Value.Inspect(plan,observe,t),token);
        public Task<IReadOnlyList<MailFolder>> ArchiveFolders(CancellationToken token=default)=>scope.Run(t=>service.Value.ArchiveFolders(t),token);
        public Task<SearchLibrary> Library(CancellationToken token=default)=>scope.Run(t=>service.Value.Library(t),token);
    }
    private sealed class ScopedTemplates(PluginScope scope,ServiceTarget<ISettingsTemplates> service) : ISettingsTemplates
    {
        public Task Export(string file,CancellationToken token) => scope.Run(async t=>{await service.Value.Export(file,t);return true;},token);
        public Task<TemplateCatalog> Open(string file,CancellationToken token) => scope.Run(t=>service.Value.Open(file,t),token);
        public Task<TemplateField[]> Fields(string file,string digest,string entry,CancellationToken token) => scope.Run(t=>service.Value.Fields(file,digest,entry,t),token);
        public Task<string> Review(string file,string digest,string entry,IReadOnlyDictionary<string,string> bindings,bool save,CancellationToken token) =>
            scope.Run(t=>service.Value.Review(file,digest,entry,bindings,save,t),token);
    }
    private sealed class ScopedRules(PluginScope scope, ServiceTarget<IReceiveRules> service) : IReceiveRules
    {
        public Task<RulePreview> Preview(ReceiveRule rule,CancellationToken token=default) => scope.Run(t=>service.Value.Preview(rule,t),token);
        public async Task Undo(Guid op,CancellationToken token=default) => await scope.Run(async t=>{await service.Value.Undo(op,t);return true;},token);
        public async Task Retry(string run,CancellationToken token=default) => await scope.Run(async t=>{await service.Value.Retry(run,t);return true;},token);
        public Task<IReadOnlyList<RuleActionLog>> Actions(string run,CancellationToken token=default) => scope.Run(t=>service.Value.Actions(run,t),token);
        public Task<RuleDashboard> Dashboard(CancellationToken token=default) => scope.Run(t=>service.Value.Dashboard(t),token);
        public Task<ReceiveRule> Save(ReceiveRule rule,CancellationToken token=default) => scope.Run(t=>service.Value.Save(rule,t),token);
        public Task<int> Process(int limit=32,CancellationToken token=default) => scope.Run(t=>service.Value.Process(limit,t),token);
    }
    private sealed class ScopedSearch(PluginScope scope, ServiceTarget<IMailSearch> service) : IMailSearch
    {
        public IReadOnlyList<SearchField> Fields => service.Value.Fields.ToArray();
        public Task<RemoteSearchPage> SearchRemote(SavedSearch definition,IProgress<SearchIndexProgress>? progress=null,CancellationToken token=default) => scope.Run(t=>service.Value.SearchRemote(definition,progress,t),token);
        public Task<SearchLibrary> Library(CancellationToken token=default) => scope.Run(t=>service.Value.Library(t),token);
        public async Task Save(SavedSearch search,CancellationToken token=default) => await scope.Run(async t=>{await service.Value.Save(search,t);return true;},token);
        public Task<SearchPage> Search(SearchRequest request,CancellationToken token=default) =>
            scope.Run(t=>service.Value.Search(request,t),token);
        public async Task RebuildBodyIndex(IProgress<SearchIndexProgress>? progress=null,CancellationToken token=default) => await scope.Run(async t => { await service.Value.RebuildBodyIndex(progress,t); return true; },token);
        public Task<ConditionResult> EvaluateSaved(string messageId,Guid savedId,CancellationToken token=default) => scope.Run(t=>service.Value.EvaluateSaved(messageId,savedId,t),token);
        public Task<ConditionResult> Evaluate(string messageId,ConditionTree condition,CancellationToken token=default) => scope.Run(t=>service.Value.Evaluate(messageId,condition,t),token);
        public Task<int> Position(SearchHit hit,SearchOrder order,CancellationToken token=default) => scope.Run(t=>service.Value.Position(hit,order,t),token);
        public Task<string> Read(SearchHit hit,CancellationToken token=default) => scope.Run(t=>service.Value.Read(hit,t),token);
    }

    private sealed class ScopedActions(PluginScope scope, ServiceTarget<IMailActionService> service) : IMailActionService
    {
        public Task<IReadOnlyList<MailActionResult>> Apply(MailActionRequest request, CancellationToken token = default) =>
            scope.Run(t => service.Value.Apply(request,t),token);
        public async Task Undo(Guid operation, CancellationToken token = default) =>
            await scope.Run(async t => { await service.Value.Undo(operation,t); return true; },token);
    }

    private sealed class ScopedOrganizer(PluginScope scope, ServiceTarget<IMailOrganizer> service) : IMailOrganizer
    {
        public Task<IReadOnlyList<ManagedFolder>> Folders(Guid account, CancellationToken token = default) =>
            scope.Run(t => service.Value.Folders(account,t),token);
        public Task<Guid> Create(Guid account, Guid? parent, string name, CancellationToken token = default) =>
            scope.Run(t => service.Value.Create(account,parent,name,t),token);
        public async Task Rename(Guid account, Guid folder, string name, CancellationToken token = default) =>
            await scope.Run(async t => { await service.Value.Rename(account,folder,name,t); return true; },token);
        public async Task DeleteEmpty(Guid account, Guid folder, CancellationToken token = default) =>
            await scope.Run(async t => { await service.Value.DeleteEmpty(account,folder,t); return true; },token);
        public Task<ManagedPage> Messages(Guid account, Guid folder, int offset, int limit, CancellationToken token = default) =>
            scope.Run(t => service.Value.Messages(account,folder,offset,limit,t),token);
        public Task<IReadOnlyList<MarkResult>> Mark(Guid account, Guid folder, string[] keys, MailMark mark, string label, CancellationToken token = default) =>
            scope.Run(t => service.Value.Mark(account,folder,keys,mark,label,t),token);
    }

    private sealed class ScopedUnifiedInbox(PluginScope scope, ServiceTarget<IUnifiedInbox> service) : IUnifiedInbox
    {
        public Task<IReadOnlyList<ReceiveStatus>> Status(CancellationToken token = default) => scope.Run(t => service.Value.Status(t), token);
        public async Task Configure(Guid account, bool automatic, int range, CancellationToken token = default) =>
            await scope.Run(async t => { await service.Value.Configure(account, automatic, range, t); return true; }, token);
        public async Task Refresh(Guid account, CancellationToken token = default) =>
            await scope.Run(async t => { await service.Value.Refresh(account, t); return true; }, token);
        public Task<UnifiedPage> Inbox(Guid? account, InboxPosition? before, int limit, CancellationToken token = default) =>
            scope.Run(t => service.Value.Inbox(account, before, limit, t), token);
        public Task<string> Open(Guid account, string key, CancellationToken token = default) =>
            scope.Run(t => service.Value.Open(account, key, t), token);
    }

    private sealed class ScopedOutbox(PluginScope scope, ServiceTarget<IOutboxService> service) : IOutboxService
    {
        private async Task Use(Func<IOutboxService, CancellationToken, Task> work, CancellationToken cancellation) =>
            await scope.Run(async t => { await work(service.Value, t); return true; }, cancellation);
        public Task<SendPlan?> GetSendPlan(Guid id,CancellationToken token=default)=>scope.Run(t=>service.Value.GetSendPlan(id,t),token);
        public async Task Schedule(Guid id,Guid account,TextMail message,SendPlan plan,CancellationToken token=default)=>await scope.Run(async t=>{await service.Value.Schedule(id,account,message,plan,t);return true;},token);
        public Task Enqueue(Guid id, Guid account, TextMail message, CancellationToken cancellation = default) =>
            Use((s,t) => s.Enqueue(id, account, message, t), cancellation);
        public Task<SendOutcome> Deliver(Guid id, CancellationToken cancellation = default) =>
            scope.Run(t => service.Value.Deliver(id, t), cancellation);
        public Task<IReadOnlyList<OutboxItem>> List(Guid account, int offset, int limit, CancellationToken cancellation = default) =>
            scope.Run(t => service.Value.List(account, offset, limit, t), cancellation);
        public Task<IReadOnlyList<string>> History(Guid id, CancellationToken cancellation = default) =>
            scope.Run(t => service.Value.History(id, t), cancellation);
        public Task Cancel(Guid id, CancellationToken cancellation = default) => Use((s,t) => s.Cancel(id, t), cancellation);
        public Task Retry(Guid id, CancellationToken cancellation = default) => Use((s,t) => s.Retry(id, t), cancellation);
        public Task SaveCopy(Guid id, CancellationToken cancellation = default) => Use((s,t) => s.SaveCopy(id, t), cancellation);
    }

    private sealed class ScopedOfflineMarks(PluginScope scope, ServiceTarget<IOfflineMarks> service) : IOfflineMarks
    {
        public async Task Queue(Guid account,string path,ImapCachedMail message,MailMark mark,CancellationToken token=default) =>
            await scope.Run(async t=>{ await service.Value.Queue(account,path,message,mark,t); return true; },token);
        public Task<IReadOnlyList<OfflineMark>> Changes(Guid account,CancellationToken token=default) =>
            scope.Run(t=>service.Value.Changes(account,t),token);
        public async Task Dismiss(Guid account,Guid edit,CancellationToken token=default) =>
            await scope.Run(async t=>{ await service.Value.Dismiss(account,edit,t); return true; },token);
    }

    private sealed class ScopedImapSync(PluginScope scope, ServiceTarget<IImapSyncService> service) : IImapSyncService
    {
        public Task<IReadOnlyList<string>> CachedFolders(Guid account, CancellationToken cancellation = default) =>
            scope.Run(t => service.Value.CachedFolders(account, t), cancellation);
        public Task<ImapCachePage> CachedPage(Guid account, string path, int offset, int limit, CancellationToken cancellation = default) =>
            scope.Run(t => service.Value.CachedPage(account, path, offset, limit, t), cancellation);
        public Task<ImapSyncResult> Sync(Guid account, string path, int recentCount, IProgress<int>? progress = null, CancellationToken cancellation = default) =>
            scope.Run(t => service.Value.Sync(account, path, recentCount, progress, t), cancellation);
        public async Task WaitForChange(Guid account, string path, ImapSyncResult previous, CancellationToken cancellation = default) =>
            await scope.Run(async t => { await service.Value.WaitForChange(account, path, previous, t); return true; }, cancellation);
        public Task<ImapReading> OpenMessage(Guid account, string path, ImapCachedMail message, bool allowDownload, CancellationToken cancellation = default) =>
            scope.Run(t => service.Value.OpenMessage(account, path, message, allowDownload, t), cancellation);
    }

    private sealed partial class ScopedProtocols(PluginScope scope, ServiceTarget<IProtocolMailService> service) : IProtocolMailService
    {
        public Task<IReadOnlyList<ProtocolPreset>> Presets(CancellationToken cancellation = default) => scope.Run(t => service.Value.Presets(t),cancellation);
        public Task<ProtocolSettings> Settings(Guid account, CancellationToken cancellation = default) => scope.Run(t => service.Value.Settings(account, t), cancellation);
        public async Task Save(Guid account, ProtocolSettings settings, string? incomingSecret, string? outgoingSecret, CancellationToken cancellation = default) => await scope.Run(async t => { await service.Value.Save(account, settings, incomingSecret, outgoingSecret, t); return true; }, cancellation);
        public Task<IReadOnlyList<RemoteFolder>> Folders(Guid account, CancellationToken cancellation = default) => scope.Run(t => service.Value.Folders(account, t), cancellation);
        public Task<RemoteMailPage> Summaries(Guid account, string folder, int offset, int limit, CancellationToken cancellation = default) => scope.Run(t => service.Value.Summaries(account, folder, offset, limit, t), cancellation);
        public Task<SendOutcome> Send(Guid account, TextMail message, CancellationToken cancellation = default) => scope.Run(t => service.Value.Send(account, message, t), cancellation);
        public Task<PopOutcome> ReceivePop(Guid account, bool deleteAfterDownload, int limit = 100, CancellationToken cancellation = default) => scope.Run(t => service.Value.ReceivePop(account, deleteAfterDownload, limit, t), cancellation);
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
    private sealed class ScopedPstImport(PluginScope scope, ServiceTarget<IPstImport> service) : IPstImport
    {
        public Task<IReadOnlyList<LocalMailboxInfo>> Targets(CancellationToken token) => scope.Run(t => service.Value.Targets(t), token);
        public Task<IReadOnlyList<PstSession>> Pending(CancellationToken token) => scope.Run(t => service.Value.Pending(t), token);
        public IReadOnlyList<ImportFormat> Formats => service.Value.Formats.ToArray();
        public Task<PstPreview> PreviewFile(string format, string file, Dictionary<string,string> parameters, CancellationToken token) =>
            scope.Run(t => service.Value.PreviewFile(format, file, parameters, t), token);
        public Task<PstPreview> Preview(string file, int? codePage, CancellationToken token) => scope.Run(t => service.Value.Preview(file, codePage, t), token);
        public Task<PstResult> Import(PstPreview preview, Guid target, IProgress<PstProgress>? progress, CancellationToken token) =>
            scope.Run(t => service.Value.Import(preview, target, progress, t), token);
    }

    private sealed class ScopedOutlookAccounts(PluginScope scope, ServiceTarget<IOutlookAccountImport> service) : IOutlookAccountImport
    {
        public Task<IReadOnlyList<string>> Profiles(CancellationToken token) => scope.Run(t => service.Value.Profiles(t), token);
        public Task<IReadOnlyList<OutlookAccountPreview>> Preview(string profile, CancellationToken token) => scope.Run(t => service.Value.Preview(profile, t), token);
        public Task<IReadOnlyList<MailAccount>> Matches(string provider, string address, CancellationToken token) => scope.Run(t => service.Value.Matches(provider, address, t), token);
        public Task<MailAccount> Import(AccountEdit edit, ProtocolSettings settings, Guid? mapTo, CancellationToken token) =>
            scope.Run(t => service.Value.Import(edit, settings, mapTo, t), token);
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
