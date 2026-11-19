using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class DraftFolderView : UserControl, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    internal DraftFolderView(ComposerPage page, Action<MailDraft> open)
    {
        var form = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing=10 };
        form.Children.Add(new TextBlock { Text="草稿箱", FontSize=28, FontWeight=FontWeight.SemiBold });
        var list = new ListBox();
        AutomationProperties.SetName(list,"草稿列表"); AutomationProperties.SetAutomationId(list,"draft-list");
        list.ItemTemplate = new FuncDataTemplate<MailDraft>((d,_) => new TextBlock { Text=d is null ? "" :
            (string.IsNullOrWhiteSpace(d.Subject)?"（无主题）":d.Subject)+"\n"+d.Identity+(string.IsNullOrWhiteSpace(d.To)?"":"  →  "+d.To),TextWrapping=TextWrapping.Wrap });
        var button = new Button { Content="继续写", IsEnabled=false }; AutomationProperties.SetAutomationId(button,"draft-edit");
        void Edit(){ if (list.SelectedItem is MailDraft d) open(d); }
        list.SelectionChanged+=(_,_)=>button.IsEnabled=list.SelectedItem is MailDraft;
        button.Click+=(_,_)=>Edit();list.DoubleTapped+=(_,_)=>Edit();
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap};
        Grid.SetRow(button,1);form.Children.Add(button);Grid.SetRow(list,2);form.Children.Add(list);Grid.SetRow(status,3);form.Children.Add(status);
        Content=form;
        AttachedToVisualTree+=async (_,_)=>{
            try
            {
                var accounts=await page.Accounts.List(_stop.Token);
                if (_stop.IsCancellationRequested) return;
                var drafts=accounts.SelectMany(a=>page.Drafts.List(a.Id)).ToArray();
                list.ItemsSource=drafts;status.Text=drafts.Length==0?"没有草稿。写邮件时会自动保存。":$"共 {drafts.Length} 封草稿";
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(Exception e){status.Text="草稿读取失败："+e.Message;}
        };
    }
    public void Dispose(){_stop.Cancel();_stop.Dispose();}
}
