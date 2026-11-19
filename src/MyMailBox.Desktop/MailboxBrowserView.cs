using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

namespace MyMailBox.Desktop;

internal sealed class MailboxBrowserView : UserControl, IDisposable
{
    private readonly Contributions _registry;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _accounts = new(), _folders = new(), _destination = new();
    private readonly ListBox _messages = new() { Height = 155 };
    private readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap }, _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _query = new(), _file = new();
    private readonly StackPanel _panel = new() { Spacing = 8, MaxWidth = 850 };
    private readonly List<(Button Button, MailboxFeatures Feature)> _actions = [];
    private CancellationTokenSource? _operation;
    private bool _disposed;
    private string? _next, _search, _delta;
    private byte[]? _readMime;
    private MailAccount? Account => _accounts.SelectedItem as MailAccount;
    private IMailboxProvider? Provider => _registry.MailboxProviders.SingleOrDefault(p => p.Type.Id == Account?.Provider);
    private MailboxIdentity Identity => new(Account!.Id, Account.Address);
    private string Folder => (_folders.SelectedItem as MailboxFolder)?.Id ?? throw new InvalidDataException("请选择目录。");
    private MailboxItem Item => _messages.SelectedItem as MailboxItem ?? throw new InvalidDataException("请选择邮件。");

    internal MailboxBrowserView(IAccountService accounts, Contributions registry,Action<ReplyIntent> respond)
    {
        _registry = registry;
        _accounts.ItemTemplate = new FuncDataTemplate<MailAccount>((a, _) => new TextBlock { Text = a?.Name + " · " + a?.Address });
        _folders.ItemTemplate = _destination.ItemTemplate = new FuncDataTemplate<MailboxFolder>((f, _) => new TextBlock { Text = f?.Name });
        _messages.ItemTemplate = new FuncDataTemplate<MailboxItem>((m, _) => new TextBlock { Text = m == null ? "" : $"{(m.Read ? "" : "未读 · ")}{m.Subject} · {m.Sender}" });
        _panel.Children.Add(new TextBlock { Text = "邮箱文件夹", FontSize = 28 });
        Field("账户", "account", _accounts);
        Action("连接并读取目录", "connect", MailboxFeatures.Read, async (p,t) =>
        {
            await p.Connect(Identity,t);
            var folders = await p.Folders(Identity,t);
            _folders.ItemsSource = folders;
            _destination.ItemsSource = folders;
            _folders.SelectedItem = folders.FirstOrDefault(f => f.Role == "inbox") ?? folders.FirstOrDefault();
            if (_folders.SelectedItem!=null) await Load(p,null,null,t);
            else _status.Text="这个邮箱还没有文件夹。";
        });
        Field("目录", "folders", _folders);
        Action("显示目录邮件", "list", MailboxFeatures.Read, (p,t) => Load(p,null,null,t));
        Field("按主题或发件人搜索", "query", _query);
        Action("在此目录搜索", "search", MailboxFeatures.Search, (p,t) => Load(p,_query.Text ?? "",null,t));
        Field("邮件", "messages", _messages);
        Action("下一页", "next", MailboxFeatures.Read, (p,t) => Load(p,_search,_next ?? throw new InvalidDataException("已经是最后一页。"),t));
        Action("阅读所选邮件", "read", MailboxFeatures.Read, async (p,t) =>
        {
            var raw = await BuiltinMailboxAdapters.ReadOriginal(p,Identity,Folder,Item.Id,t);
            var parsed = await new MimeReader().Parse(raw,t);
            _readMime=raw;
            _body.Text = parsed.Subject + Environment.NewLine + parsed.Text;
            _status.Text = $"已读取 {raw.Length:N0} 字节。";
        });
        Id(_body, "body"); _panel.Children.Add(_body);
        var replies=new WrapPanel();
        ReplyButtons.Add(replies,"provider",()=>_readMime!=null?(_messages.SelectedItem as MailboxItem)?.Id:null,
            intent=>respond(intent with{ ReadAccount=Account!.Id,ReadMime=_readMime }),text=>_status.Text=text);
        _panel.Children.Add(replies);
        Action("切换已读状态", "mark", MailboxFeatures.Mark, async (p,t) =>
        {
            var selected = Item;
            await p.Mark(Identity,Folder,selected.Id,!selected.Read,t);
            await Load(p,_search,null,t);
            _status.Text = selected.Read ? "已标为未读。" : "已标为已读。";
        });
        Field("目标目录", "target", _destination);
        Action("移动所选邮件", "move", MailboxFeatures.Move, async (p,t) =>
        {
            var destination = (_destination.SelectedItem as MailboxFolder) ?? throw new InvalidDataException("请选择目标目录。");
            if (destination.Id == Folder) throw new InvalidDataException("请选择其他目录。");
            await p.Move(Identity,Folder,Item.Id,destination.Id,t);
            await Load(p,_search,null,t);
            _status.Text = "已移动到 " + destination.Name + "。";
        });
        var extraStart=_panel.Children.Count;
        Field("要上传的邮件文件", "file", _file);
        var browse=new Button{Content="选择邮件文件…"};Id(browse,"browse-file");
        browse.Click+=async(_,_)=>{var picker=TopLevel.GetTopLevel(this)?.StorageProvider;if(picker is null)return;
            var file=(await picker.OpenFilePickerAsync(new(){Title="选择 EML 文件"})).FirstOrDefault();if(file is not null)_file.Text=file.Path.LocalPath;};
        _panel.Children.Add(browse);
        Action("将 EML 上传到此目录", "upload", MailboxFeatures.Upload, async (p,t) =>
        {
            await using var raw = File.OpenRead(_file.Text?.Trim() ?? "");
            if (raw.Length > MimeReader.MaxMessageBytes) throw new InvalidDataException("EML 超过 25 MiB。");
            await p.Upload(Identity,Folder,raw,t);
            await Load(p,null,null,t);
            _status.Text = "已上传邮件。";
        });
        Action("查看容量", "quota", MailboxFeatures.Quota, async (p,t) =>
        {
            var quota = await p.Quota(Identity,t);
            _status.Text = $"已使用 {quota.Used:N0} 字节，总容量 {(quota.Limit?.ToString("N0") ?? "未知")} 字节。";
        });
        Action("检查目录的增量变化", "delta", MailboxFeatures.Delta, async (p,t) =>
        {
            var delta = await p.Delta(Identity,Folder,_delta,50,t);
            _delta = delta.Cursor;
            _status.Text = $"新增或更新 {delta.Changed.Length} 封，移出 {delta.Removed.Length} 封。" + (delta.More ? "可继续检查下一批。" : "已到最新位置。");
        });
        var extra=new StackPanel{Spacing=6};
        foreach(var child in _panel.Children.Skip(extraStart).ToArray()){_panel.Children.Remove(child);extra.Children.Add(child);}
        _panel.Children.Add(new Expander{Header="导入邮件、容量和同步",Content=extra});
        var cancel = new Button { Content = "取消当前操作" };
        Id(cancel,"cancel"); cancel.Click += (_,_) => _operation?.Cancel();
        Id(_status,"status"); Content = PageFeedback.Layout(_panel,_status,cancel);
        _accounts.SelectionChanged += (_,_) => { Reset(); Refresh(); };
        _folders.SelectionChanged += async (_,_) => { ClearMessages(); if(_operation==null && _folders.SelectedItem!=null && Provider is {} p)await Run(t=>Load(p,null,null,t)); };
        _messages.SelectionChanged += (_,_) => { _body.Text = ""; _readMime=null; };
        registry.Changed += RegistryChanged;
        AttachedToVisualTree += async (_,_) => await Run(async t =>
        {
            _accounts.ItemsSource = (await accounts.List(t)).Where(a => a.Enabled && (a.Provider.Contains('/') || registry.MailboxProviders.Any(p => p.Type.Id == a.Provider))).ToArray();
            _accounts.SelectedIndex = 0;
            Describe();
        });
    }
    private void RegistryChanged() { if (_disposed) return; if (Provider == null) { _operation?.Cancel(); Reset(); Describe(); } Refresh(); }
    private void Describe() => _status.Text = Account == null ? "请先添加邮箱账户。" : Provider == null ? $"需要启用邮箱插件 {Account.Provider}。" : "选择账户后连接以读取目录。";
    private void Reset() { _folders.ItemsSource = null; _destination.ItemsSource = null; ClearMessages(); Describe(); }
    private void ClearMessages() { _readMime=null; _messages.ItemsSource = null; _body.Text = ""; _next = _search = _delta = null; }
    private async Task Load(IMailboxProvider p,string? query,string? cursor,CancellationToken t)
    {
        var result = query == null ? await p.List(Identity,Folder,cursor,25,t) : await p.Search(Identity,Folder,query,cursor,25,t);
        _search = query; _next = result.Next;
        _messages.ItemsSource = result.Items; _messages.SelectedIndex = 0;
        _status.Text = $"本页 {result.Items.Length} 封邮件。" + (_next == null ? "" : "可继续查看下一页。");
    }
    private static void Id(Control c,string id) => AutomationProperties.SetAutomationId(c,"provider-" + id);
    private void Field(string title,string id,Control c)
    {
        Id(c,id); AutomationProperties.SetName(c,title); c.HorizontalAlignment = HorizontalAlignment.Stretch;
        _panel.Children.Add(new TextBlock { Text = title }); _panel.Children.Add(c);
    }
    private void Action(string title,string id,MailboxFeatures feature,Func<IMailboxProvider,CancellationToken,Task> action)
    {
        var button = new Button { Content = title }; Id(button,id);
        button.Click += async (_,_) => await Run(t => action(Provider ?? throw new InvalidOperationException("需要启用此邮箱插件。"),t));
        _actions.Add((button,feature)); _panel.Children.Add(button);
    }
    private void Refresh()
    {
        var features = Provider?.Type.Features ?? 0;
        foreach (var (button,feature) in _actions) button.IsVisible = features.HasFlag(feature);
        _panel.IsEnabled = _operation == null;
    }
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if (_disposed || _operation != null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation; Refresh();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { _status.Text = Provider == null ? "邮箱插件已停用。" : "操作已取消。"; }
        catch (Exception e) { _status.Text = e is InvalidDataException or InvalidOperationException or UnauthorizedAccessException ? e.Message : "操作未完成，请检查文件路径、插件授权及连接。"; }
        finally { _operation = null; if (!_disposed) Refresh(); }
    }
    public void Dispose() { _disposed = true; _registry.Changed -= RegistryChanged; _lifetime.Cancel(); _lifetime.Dispose(); }
}
