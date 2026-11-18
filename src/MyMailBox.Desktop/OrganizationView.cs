using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class OrganizationView : UserControl, IDisposable
{
    private readonly OrganizationPage _page;
    private readonly CancellationTokenSource _stop = new();
    private readonly ComboBox _accounts = new(), _folders = new();
    private readonly TextBox _name = new(), _label = new();
    private readonly CheckBox _child = new() { Content = "在所选目录内新建" };
    private readonly ListBox _list = new() { MinHeight = 120, MaxHeight = 230, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _scope = new() { TextWrapping = TextWrapping.Wrap }, _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _panel = new() { Spacing = 8, MaxWidth = 900 };
    private Button _prev = null!, _next = null!, _rename = null!, _delete = null!, _create = null!;
    private int _offset;
    private bool _more, _busy;
    private Guid? _confirm;
    private ManagedFolder? Folder => _folders.SelectedItem as ManagedFolder;
    private MailAccount Account => _accounts.SelectedItem as MailAccount ?? throw new InvalidDataException("请先选择账户。");

    public OrganizationView(OrganizationPage page)
    {
        _page = page;
        _panel.Children.Add(new TextBlock { Text = "目录与标记", FontSize = 28, FontWeight = FontWeight.SemiBold });
        _accounts.ItemTemplate = new FuncDataTemplate<MailAccount>((a,_) => new TextBlock { Text = a?.Name });
        _folders.ItemTemplate = new FuncDataTemplate<ManagedFolder>((f,_) => new TextBlock { Text = f?.Path });
        Field(_panel, "账户", _accounts, "org-account");
        Action(_panel, "读取目录", "org-refresh", () => LoadFolders(Folder?.Id));
        Field(_panel, "目录", _folders, "org-folder");
        var edit = new StackPanel { Spacing = 6 };
        Field(edit,"目录名称",_name,"org-name"); edit.Children.Add(_child);
        var folderActions = new WrapPanel();
        _create = Action(folderActions,"新建目录","org-create",async () =>
        {
            var id = await _page.Organizer.Create(Account.Id,_child.IsChecked == true ? Folder?.Id : null,_name.Text ?? "",_stop.Token);
            await LoadFolders(id); _status.Text = "已创建目录。";
        });
        _rename = Action(folderActions,"重命名所选目录","org-rename",async () =>
        {
            var id = Folder!.Id; await _page.Organizer.Rename(Account.Id,id,_name.Text ?? "",_stop.Token);
            await LoadFolders(id); _status.Text = "目录已重命名。";
        });
        _delete = Action(folderActions,"删除空目录","org-delete",async () =>
        {
            if (_confirm != Folder!.Id)
            {
                _confirm = Folder.Id; _status.Text = $"再次点击以确认删除空目录「{Folder.Path}」。"; return;
            }
            _confirm = null; await _page.Organizer.DeleteEmpty(Account.Id,Folder.Id,_stop.Token);
            await LoadFolders(null); _status.Text = "空目录已删除，引用此目录的选择需要修复。";
        });
        edit.Children.Add(folderActions); _panel.Children.Add(new Expander { Header = "管理目录",Content = edit });
        _panel.Children.Add(_scope);
        Field(_panel,"邮件（可多选）",_list,"org-messages");
        var nav = new WrapPanel();
        _prev = Action(nav,"上一页","org-prev",() => LoadMessages(_offset-25));
        _next = Action(nav,"下一页","org-next",() => LoadMessages(_offset+25)); _panel.Children.Add(nav);
        var marks = new WrapPanel();
        foreach (var (mark,title) in new[] { (MailMark.Read,"设为已读"), (MailMark.Unread,"设为未读"),(MailMark.Star,"加星"),(MailMark.Unstar,"取消星标") })
            Action(marks,title,"org-" + mark,() => Mark(mark));
        _panel.Children.Add(marks);
        Field(_panel,"本地标签",_label,"org-label");
        var tagging = new WrapPanel();
        Action(tagging,"添加标签","org-add-label",() => Mark(MailMark.AddLabel));
        Action(tagging,"移除标签","org-remove-label",() => Mark(MailMark.RemoveLabel)); _panel.Children.Add(tagging);
        if (_page.Actions is not null) AddMessageActions();
        if (_page.Transfers is not null) AddTransfers();
        AutomationProperties.SetAutomationId(_status,"org-status");
        _panel.Children.Add(_status);
        Content=PageFeedback.Layout(_panel,_status,_stopTransfer);
        _accounts.SelectionChanged += (_,_) =>
        {
            _historyOffset = 0; _archiveTarget.ItemsSource = null; _history.ItemsSource = null;
            ResetActions(); _destination.ItemsSource = null; _folders.ItemsSource = null; _list.Items.Clear(); _confirm = null; _more = false; _offset = 0;
            _scope.Text = _accounts.SelectedItem is not MailAccount a ? "" : a.Provider switch
            {
                "imap" or "imap-smtp" => "已读和星标同步至服务器。标签仅在本地保存。",
                "pop3" => "POP3 不提供服务器目录和标记，已下载邮件的整理结果仅在本地保存。",
                "local" => "目录、已读、星标和标签保存在本地邮箱。", _ => "此提供者尚不支持目录整理。"
            };
            _status.Text = "选择「读取目录」以浏览当前账户。"; Buttons();
        };
        _folders.SelectionChanged += async (_,_) =>
        {
            _confirm = null;
            if (!_busy && Folder is not null) await Run(() => LoadMessages(0));
            Buttons();
        };
        AttachedToVisualTree += async (_,_) => await Run(async () =>
        {
            var accounts = await _page.Accounts.List(_stop.Token);
            _accounts.ItemsSource = accounts.Where(a => a.Enabled).ToArray();
            _accounts.SelectedItem = accounts.FirstOrDefault(a => a.Enabled && a.Provider == "local") ?? accounts.FirstOrDefault(a => a.Enabled);
            if (_accounts.SelectedItem is MailAccount { Provider: "local" }) await LoadFolders(null);
        });
    }

    private static void Field(Panel parent,string name,Control input,string id)
    {
        parent.Children.Add(new TextBlock { Text=name }); AutomationProperties.SetName(input,name);
        AutomationProperties.SetAutomationId(input,id); parent.Children.Add(input);
    }

    private Button Action(Panel panel,string name,string id,Func<Task> command)
    {
        var button = new Button { Content = name, Margin = new Avalonia.Thickness(0,0,5,2) };
        AutomationProperties.SetAutomationId(button,id); button.Click += async (_,_) => await Run(command); panel.Children.Add(button); return button;
    }

    private async Task Run(Func<Task> work)
    {
        if (_busy) return;
        _busy = true; _panel.IsEnabled = false;
        try { await work(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) _status.Text = e.Message; }
        finally { _busy = false; if (!_stop.IsCancellationRequested) { _panel.IsEnabled = true; Buttons(); } }
    }

    private void Buttons()
    {
        _rename.IsEnabled = _delete.IsEnabled = Folder?.CanRename == true;
        _create.IsEnabled = _accounts.SelectedItem is MailAccount { Provider: "local" or "imap" or "imap-smtp" };
        _prev.IsEnabled = _offset > 0; _next.IsEnabled = _more;
    }

    private async Task LoadFolders(Guid? selected)
    {
        var items = await _page.Organizer.Folders(Account.Id,_stop.Token);
        _destination.ItemsSource = items; _destination.SelectedIndex = -1;
        _folders.ItemsSource = items;
        _folders.SelectedItem = items.FirstOrDefault(f => f.Id == selected) ?? items.FirstOrDefault(f => f.Role == "inbox") ?? items.FirstOrDefault();
        await LoadMessages(0);
        if (_page.Transfers is not null) await LoadTransferOptions();
    }

    private static string Caption(ManagedMessage m) =>
        $"{(m.Starred ? "★ " : "")}{m.Subject}\n{(m.Read ? "已读" : "未读")}" + (m.Labels.Length > 0 ? " · " + string.Join("、",m.Labels) : "");

    private async Task LoadMessages(int offset)
    {
        var page = Folder is { } folder ? await _page.Organizer.Messages(Account.Id,folder.Id,offset,25,_stop.Token) : new ManagedPage([],false);
        _pendingDelete = null;
        _offset = offset; _more = page.HasMore;
        _list.Items.Clear();
        foreach (var m in page.Items) _list.Items.Add(new ListBoxItem { Tag = m, Content = Caption(m) });
        _status.Text = page.Items.Count == 0 ? "当前目录没有邮件。" : $"第 {offset/25+1} 页，共显示 {page.Items.Count} 封。";
    }

    private async Task Mark(MailMark mark)
    {
        var selected = _list.SelectedItems?.OfType<ListBoxItem>().ToDictionary(i => ((ManagedMessage)i.Tag!).Key) ?? [];
        if (Folder is null || selected.Count == 0) throw new InvalidDataException("请先选择需要整理的邮件。");
        var results = await _page.Organizer.Mark(Account.Id,Folder.Id,selected.Keys.ToArray(),mark,_label.Text ?? "",_stop.Token);
        var lines = new List<string>();
        foreach (var r in results)
        {
            var item = selected[r.Key]; var m = (ManagedMessage)item.Tag!;
            lines.Add($"{m.Subject} · {r.Detail}");
            if (r.Updated is { } update) { item.Tag = update; item.Content = Caption(update); }
        }
        _status.Text = $"成功 {results.Count(r => r.Success)}，失败 {results.Count(r => !r.Success)}\n" + string.Join("\n",lines);
    }

    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
