using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class OrganizationView
{
    private readonly ComboBox _destination = new();
    private Guid[] _undo = [];
    private Button? _undoButton;
    private MailActionRequest? _pendingDelete;

    private void AddMessageActions()
    {
        var actions = new StackPanel { Spacing = 6 };
        _destination.ItemTemplate = new FuncDataTemplate<ManagedFolder>((f,_) => new TextBlock { Text=f?.Path });
        Field(actions,"目标目录（复制、移动或指定归档目录）",_destination,"org-destination");
        var buttons = new WrapPanel();
        foreach (var (kind,name) in new[] { (MailAction.Copy,"复制"),(MailAction.Move,"移动"),(MailAction.Archive,"归档"),
                     (MailAction.Trash,"移入垃圾箱"),(MailAction.DeletePermanently,"永久删除") })
            Action(buttons,name,"org-action-"+kind,() => ApplyAction(kind));
        _undoButton = Action(buttons,"撤销上次移动","org-undo",UndoActions);
        _undoButton.IsVisible = false;
        actions.Children.Add(buttons);
        _panel.Children.Add(new Expander { Header="移动、复制与删除", Content=actions });
    }

    private void ResetActions()
    {
        _pendingDelete = null; _undo = [];
        if (_undoButton is not null) _undoButton.IsVisible = false;
    }

    private async Task ApplyAction(MailAction kind)
    {
        var selected = _list.SelectedItems?.OfType<ListBoxItem>().ToDictionary(i=>((ManagedMessage)i.Tag!).Key) ?? [];
        var folder = Folder ?? throw new InvalidDataException("请选择来源目录。");
        if (selected.Count == 0) throw new InvalidDataException("请选择需要处理的邮件。");
        var request = new MailActionRequest(Account.Id,folder.Id,selected.Keys.ToArray(),kind,(_destination.SelectedItem as ManagedFolder)?.Id);
        if (kind == MailAction.DeletePermanently &&
            (_pendingDelete is not { } previous || previous.Account!=request.Account || previous.Source!=request.Source || !previous.Keys.SequenceEqual(request.Keys)))
        {
            _pendingDelete = request;
            _status.Text = $"将永久删除「{folder.Path}」中选定的 {selected.Count} 封邮件。再次点击「永久删除」确认，此操作无法撤销。";
            return;
        }
        ResetActions();
        var results = await _page.Actions!.Apply(request,_stop.Token);
        _undo = results.Where(r=>r.UndoId.HasValue).Select(r=>r.UndoId!.Value).ToArray();
        _undoButton!.IsVisible = _undo.Length > 0;
        var lines = results.Select(r=>$"{((ManagedMessage)selected[r.Key].Tag!).Subject} · {r.Detail}").ToArray();
        if (kind != MailAction.Copy)
            foreach (var r in results.Where(r=>r.Success)) _list.Items.Remove(selected[r.Key]);
        _status.Text = $"成功 {results.Count(r=>r.Success)}，失败 {results.Count(r=>!r.Success)}\n" + string.Join("\n",lines);
    }

    private async Task UndoActions()
    {
        var failures = new List<string>();
        var done = 0;
        foreach (var id in _undo)
        {
            try { await _page.Actions!.Undo(id,_stop.Token); done++; }
            catch (InvalidOperationException e) { failures.Add(e.Message); }
        }
        ResetActions(); await LoadMessages(_offset);
        _status.Text = $"已撤销 {done} 项。" + string.Join("\n",failures);
    }
}
