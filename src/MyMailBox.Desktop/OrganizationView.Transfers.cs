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
    private readonly TextBlock _transferDetail = new() { TextWrapping = TextWrapping.Wrap };
    private Button? _continueTransfer, _cancelTransfer, _previousTransfers, _nextTransfers;
    private int _historyOffset;

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
        _continueTransfer = Action(historyActions, "继续所选操作", "transfer-resume", async () =>
        {
            if (_history.SelectedItem is not MailTransfer transfer) throw new InvalidDataException("请先选择归档记录。");
            await ContinueTransfers([transfer]);
        });
        _cancelTransfer = Action(historyActions, "结束后续处理并保留副本", "transfer-cancel", async () =>
        {
            if (_history.SelectedItem is not MailTransfer t) return;
            await _page.Transfers!.Cancel(t.Request.Id,_stop.Token);
            await LoadHistory(t.Request.Id);
        });
        _previousTransfers = Action(historyActions,"上一页记录","transfer-prev",async () => { _historyOffset-=25; await LoadHistory(null); });
        _nextTransfers = Action(historyActions,"下一页记录","transfer-next",async () => { _historyOffset+=25; await LoadHistory(null); });
        panel.Children.Add(historyActions);
        AutomationProperties.SetAutomationId(_transferDetail,"transfer-detail"); panel.Children.Add(_transferDetail);
        _history.SelectionChanged += async (_,_) =>
        {
            var t = _history.SelectedItem as MailTransfer;
            _continueTransfer.IsEnabled = _cancelTransfer.IsEnabled = t is not null && t.Phase is not ("done" or "canceled");
            _transferDetail.Text = "";
            if (t is null) return;
            try
            {
                var events = await _page.Transfers!.History(t.Request.Id,_stop.Token);
                if (!ReferenceEquals(t,_history.SelectedItem)) return;
                _transferDetail.Text = $"{t.SourcePath} → {t.DestinationPath} · {PhaseName(t.Phase)}\n" +
                    string.Join("\n",events.Select(e=>$"{e.Time.LocalDateTime:T} · {PhaseName(e.Phase)} · {e.Detail}"));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!_stop.IsCancellationRequested) _transferDetail.Text=e.Message; }
        };
        _panel.Children.Add(new Expander { Header = "跨邮箱归档", Content = panel });
        AutomationProperties.SetAutomationId(_stopTransfer, "transfer-stop");
        _stopTransfer.Click += (_, _) => _transferRun?.Cancel();
    }

    private static string PhaseName(string phase) => phase switch
    {
        "planned"=>"等待读取来源", "stored"=>"目标已写入", "verified"=>"完整性已验证",
        "cleaning"=>"来源清理待确认", "done"=>"已完成", "canceled"=>"已停止", _=>phase
    };

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
        var items = await _page.Transfers!.Recent(Account.Id, _historyOffset, 26, _stop.Token);
        _previousTransfers!.IsEnabled = _historyOffset > 0; _nextTransfers!.IsEnabled = items.Count > 25;
        _history.ItemsSource = items.Take(25).ToArray();
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
                var progress = new Progress<MailTransfer>(s=>
                {
                    if (_transferRun == run) _status.Text=$"{s.SourcePath} → {s.DestinationPath} · {PhaseName(s.Phase)}";
                });
                var result = await _page.Transfers!.Continue(transfer.Request.Id, progress, run.Token);
                lines.Add($"{transfer.Request.Key} · {result.Detail}");
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        { lines.Add("已停止，已写入的副本和操作记录已保留。"); }
        finally { _transferRun = null; _stopTransfer.IsVisible = false; }
        if (_stop.IsCancellationRequested) return;
        _historyOffset = 0; await LoadHistory(planned[^1].Request.Id);
        if (planned.Any(t=>t.Request.Move) && Folder is not null)
        {
            try { await LoadMessages(_offset); }
            catch (Exception e) when (!_stop.IsCancellationRequested) { lines.Add("刷新来源目录失败：" + e.Message); }
        }
        _status.Text = string.Join("\n", lines);
    }
}
