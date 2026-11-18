using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class OrganizationView
{
    private readonly ComboBox _archiveTarget = new();
    private readonly ListBox _history = new() { MaxHeight = 200 };
    private readonly Button _stopTransfer = new() { Content = "停止当前归档", IsVisible = false };
    private CancellationTokenSource? _transferRun;

    private void AddTransfers()
    {
        var panel = new StackPanel { Spacing = 6 };
        _archiveTarget.ItemTemplate = new FuncDataTemplate<MailFolder>((f, _) => new TextBlock { Text = f?.Path });
        Field(panel, "其他本地邮箱的目标目录", _archiveTarget, "transfer-target");
        var actions = new WrapPanel();
        Action(actions, "复制并保留来源", "transfer-copy", () => PlanTransfers(false));
        Action(actions, "移动（验证后清理来源）", "transfer-move", () => PlanTransfers(true));
        panel.Children.Add(actions);
        _history.ItemTemplate = new FuncDataTemplate<MailTransfer>((m, _) => new TextBlock
        {
            Text = m is null ? "" : $"{m.SourcePath} → {m.DestinationPath}\n{m.Detail}",
            TextWrapping = TextWrapping.Wrap
        });
        Field(panel, "此账户的归档记录", _history, "transfer-history");
        var historyActions = new WrapPanel();
        Action(historyActions, "刷新记录", "transfer-refresh", () => LoadHistory(null));
        Action(historyActions, "继续所选操作", "transfer-resume", async () =>
        {
            if (_history.SelectedItem is not MailTransfer transfer) throw new InvalidDataException("请先选择归档记录。");
            await ContinueTransfers([transfer]);
        });
        panel.Children.Add(historyActions);
        _panel.Children.Add(new Expander { Header = "跨邮箱归档", Content = panel });
        AutomationProperties.SetAutomationId(_stopTransfer, "transfer-stop");
        _stopTransfer.Click += (_, _) => _transferRun?.Cancel();
    }

    private async Task LoadTransferOptions()
    {
        var selected = (_archiveTarget.SelectedItem as MailFolder)?.Id;
        var items = await _page.Transfers!.Destinations(Account.Id, _stop.Token);
        _archiveTarget.ItemsSource = items;
        _archiveTarget.SelectedItem = items.FirstOrDefault(f => f.Id == selected) ?? items.FirstOrDefault();
        await LoadHistory(null);
    }

    private async Task LoadHistory(Guid? selected)
    {
        selected ??= (_history.SelectedItem as MailTransfer)?.Request.Id;
        var items = await _page.Transfers!.Recent(Account.Id, 0, 50, _stop.Token);
        _history.ItemsSource = items;
        _history.SelectedItem = items.FirstOrDefault(t => t.Request.Id == selected) ?? items.FirstOrDefault();
    }

    private async Task PlanTransfers(bool move)
    {
        if (_archiveTarget.SelectedItem is not MailFolder target || Folder is not { } source)
            throw new InvalidDataException("请选择来源与另一本地邮箱的目标目录。");
        var selected = _list.SelectedItems?.OfType<ListBoxItem>().Select(i => ((ManagedMessage)i.Tag!).Key).ToArray() ?? [];
        if (selected.Length == 0) throw new InvalidDataException("请先选择要归档的邮件。");
        var planned = new List<MailTransfer>();
        foreach (var key in selected)
            planned.Add(await _page.Transfers!.Plan(new(Guid.NewGuid(), Account.Id, source.Id, key, target.Id, move), _stop.Token));
        await ContinueTransfers(planned);
    }

    private async Task ContinueTransfers(IReadOnlyList<MailTransfer> planned)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _transferRun = run; _stopTransfer.IsVisible = true;
        var lines = new List<string>();
        try
        {
            foreach (var transfer in planned)
            {
                var result = await _page.Transfers!.Continue(transfer.Request.Id, null, run.Token);
                lines.Add($"{transfer.Request.Key} · {result.Detail}");
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        { lines.Add("已停止，已写入的副本和操作记录已保留。"); }
        finally { _transferRun = null; _stopTransfer.IsVisible = false; }
        if (_stop.IsCancellationRequested) return;
        await LoadMessages(_offset); await LoadHistory(planned[^1].Request.Id);
        _status.Text = string.Join("\n", lines);
    }
}
