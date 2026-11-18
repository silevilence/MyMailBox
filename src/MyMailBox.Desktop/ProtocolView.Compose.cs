using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed partial class ProtocolView
{
    private readonly StackPanel _compose = new() { Spacing = 8 };
    private readonly TextBox _to = new(), _cc = new(), _bcc = new(), _subject = new(), _bodyText = new() { AcceptsReturn = true, MinHeight = 160, TextWrapping = TextWrapping.Wrap };
    private Button? _sendButton;
    private bool _submitted;
    private Guid _submission = Guid.NewGuid();
    partial void AddCompose(List<TabItem> items)
    {
        _compose.Children.Add(new TextBlock { Text = "以所选账户地址发送纯文本邮件。服务器会检查该身份是否获准发送。", TextWrapping = TextWrapping.Wrap });
        Add(_compose, "收件人 To", _to, "smtp-to"); Add(_compose, "抄送 Cc", _cc, "smtp-cc"); Add(_compose, "密送 Bcc", _bcc, "smtp-bcc");
        Add(_compose, "主题", _subject, "smtp-subject"); Add(_compose, "正文", _bodyText, "smtp-body");
        _editors.AddRange([_to, _cc, _bcc, _subject, _bodyText]);
        _sendButton = Action("发送这封邮件", "smtp-send", Send); _compose.Children.Add(_sendButton);
        _compose.Children.Add(Action("新建下一封（清空当前内容）", "smtp-new", () =>
        {
            _to.Text = _cc.Text = _bcc.Text = _subject.Text = _bodyText.Text = ""; _submitted = false; _submission = Guid.NewGuid(); _status.Text = "已新建空白邮件。"; return Task.CompletedTask;
        }));
        items.Add(new TabItem { Header = "发送邮件", Content = _compose });
    }
    partial void UpdateCompose()
    {
        var canSend = _selected?.Capabilities.HasFlag(AccountCapabilities.Send) == true;
        _compose.IsVisible = canSend;
        if (_sendButton is not null) _sendButton.IsEnabled = !_busy && canSend && !_submitted;
    }
    private async Task Send()
    {
        var account = Selected().Id;
        var mail = new TextMail(_to.Text ?? "", _cc.Text ?? "", _bcc.Text ?? "", _subject.Text ?? "", _bodyText.Text ?? "");
        SendOutcome result;
        if (_page.Outbox is { } outbox)
        {
            await outbox.Enqueue(_submission, account, mail, Token);
            _submitted = true;
            result = await outbox.Deliver(_submission, Token);
            await LoadOutbox(0);
        }
        else
        {
            result = await _page.Service.Send(account, mail, Token);
            _submitted = result.State is "sent" or "partial" or "unknown";
        }
        if (_disposed) return;
        _status.Text = result.Message + (result.Accepted.Count > 0 ? "\n已接收：" + string.Join(", ", result.Accepted) : "") + (result.Rejected.Count > 0 ? "\n被拒绝：" + string.Join(", ", result.Rejected) : "");
    }
}
