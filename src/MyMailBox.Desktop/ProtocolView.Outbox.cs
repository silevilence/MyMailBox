using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed partial class ProtocolView
{
    private readonly ListBox _outboxList = new() { MinHeight = 120, MaxHeight = 240 };
    private readonly TextBlock _outboxDetail = new() { TextWrapping = TextWrapping.Wrap };
    private Button? _retryMail, _cancelMail, _copyMail, _olderMail, _newerMail;
    private int _outboxOffset;
    private bool _outboxMore;

    private void AddOutbox(List<TabItem> tabs)
    {
        if (_page.Outbox is null) return;
        var panel = new StackPanel { Spacing = 8 };
        _outboxList.ItemTemplate = new FuncDataTemplate<OutboxItem>((m, _) => new TextBlock
        {
            Text = m is null ? "" : $"{StateName(m.State)} · {m.Subject}\n{m.Created.LocalDateTime:g}" + (m.CopyPending ? " · 副本待保存" : ""),
            TextWrapping = TextWrapping.Wrap
        });
        Add(panel, "当前账户的发件记录", _outboxList, "outbox-list");
        var actions = new WrapPanel();
        actions.Children.Add(Action("刷新发件箱", "outbox-refresh", () => LoadOutbox(_outboxOffset)));
        _newerMail = Action("上一页", "outbox-prev", () => LoadOutbox(_outboxOffset - 20));
        _olderMail = Action("下一页", "outbox-next", () => LoadOutbox(_outboxOffset + 20));
        _cancelMail = Action("取消待发", "outbox-cancel", () => ChangeOutbox((s,id) => s.Cancel(id, Token)));
        _retryMail = Action("重试未接收的收件人", "outbox-retry", () => ChangeOutbox((s,id) => s.Retry(id, Token)));
        _copyMail = Action("补存本地副本", "outbox-copy", () => ChangeOutbox((s,id) => s.SaveCopy(id, Token)));
        foreach (var button in new[] { _newerMail, _olderMail, _cancelMail, _retryMail, _copyMail }) actions.Children.Add(button);
        panel.Children.Add(actions);
        AutomationProperties.SetAutomationId(_outboxDetail, "outbox-detail");
        panel.Children.Add(_outboxDetail);
        _outboxList.SelectionChanged += async (_, _) =>
        {
            OutboxButtons();
            var selected = _outboxList.SelectedItem as OutboxItem;
            _outboxDetail.Text = selected?.Detail ?? "";
            if (selected is null) return;
            try
            {
                var history = await _page.Outbox.History(selected.Id, _lifetime.Token);
                if (!_disposed && ReferenceEquals(_outboxList.SelectedItem, selected))
                    _outboxDetail.Text = selected.Detail + (selected.Accepted.Length > 0 ? "\n已获接收：" + string.Join(", ", selected.Accepted) : "") +
                        (selected.Remaining.Length > 0 ? "\n尚无接收确认：" + string.Join(", ", selected.Remaining) : "") +
                        "\n" + string.Join("\n", history);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!_disposed) _status.Text = e.Message; }
        };
        tabs.Add(new TabItem { Header = "发件箱", Content = panel });
    }

    private static string StateName(string state) => state switch
    {
        "scheduled" => "已预约", "held" => "错过或已编辑，待确认", "pending" => "待发送", "sending" => "正在发送", "sent" => "发送成功", "partial" => "部分成功",
        "unknown" => "结果待确认", "rejected" => "未能发送", "canceled" => "已取消", _ => state
    };

    private void OutboxButtons()
    {
        if (_retryMail is null) return;
        var selected = _outboxList.SelectedItem as OutboxItem;
        _cancelMail!.IsEnabled = !_busy && selected?.State is "pending" or "scheduled" or "held";
        _retryMail.IsEnabled = !_busy && selected?.State is "partial" or "pending" or "rejected";
        _copyMail!.IsEnabled = !_busy && selected?.CopyPending == true;
        _newerMail!.IsEnabled = !_busy && _outboxOffset > 0;
        _olderMail!.IsEnabled = !_busy && _outboxMore;
    }

    private async Task ChangeOutbox(Func<IOutboxService, Guid, Task> change)
    {
        if (_outboxList.SelectedItem is not OutboxItem selected) return;
        await change(_page.Outbox!, selected.Id);
        await LoadOutbox(_outboxOffset);
    }

    private async Task LoadOutbox(int offset)
    {
        if (_page.Outbox is null || _selected is null) return;
        var old = (_outboxList.SelectedItem as OutboxItem)?.Id;
        var items = await _page.Outbox.List(_selected.Id, offset, 21, Token);
        if (_disposed) return;
        var page = items.Take(20).ToArray();
        _outboxList.ItemsSource = page;
        _outboxList.SelectedItem = page.FirstOrDefault(i => i.Id == old) ?? page.FirstOrDefault();
        _outboxOffset = offset; _outboxMore = items.Count > 20;
        OutboxButtons();
    }
}
