using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace MyMailBox.Desktop;

public sealed partial class ProtocolView
{
    private readonly StackPanel _pop = new() { Spacing = 10 };
    private readonly CheckBox _deleteAfterDownload = new() { Content = "本次收取成功保存并校验后，删除服务器上相应副本" };
    partial void AddPop(List<TabItem> items)
    {
        _pop.Children.Add(new TextBlock { Text = "POP3 邮件下载到本地“POP3 已下载”，没有可操作的远端目录树。默认保留服务器副本；发送请另选 SMTP 账户。", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(_deleteAfterDownload, "pop-delete"); _pop.Children.Add(_deleteAfterDownload); _editors.Add(_deleteAfterDownload);
        _pop.Children.Add(Action("收取本批邮件", "pop-receive", async () =>
        {
            var remove = _deleteAfterDownload.IsChecked == true;
            var result = await _page.Service.ReceivePop(Selected().Id, remove, cancellation: Token);
            if (!_disposed) { _status.Text = result.Message + "\n可在“本地邮箱”选择下载目录阅读。"; _deleteAfterDownload.IsChecked = false; }
        }));
        items.Add(new TabItem { Header = "POP3 收取", Content = _pop });
    }
    partial void UpdatePop() => _pop.IsVisible = _selected?.Provider == "pop3";
}
