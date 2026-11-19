using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MyMailBox.Contracts;
using MyMailBox.Mail;

namespace MyMailBox.Desktop;

internal static class OriginalMailWindow
{
    internal static async Task Show(Window owner, byte[] raw, CancellationToken token)
    {
        var mail=await new MimeReader().Parse(raw,token);
        var window=new Window { Title=mail.Subject, Width=Math.Min(830,owner.Bounds.Width),Height=Math.Min(650,owner.Bounds.Height),
            MinWidth=350,MinHeight=270,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=owner.FontFamily };
        var layout=new Grid { Margin=new Thickness(18),RowDefinitions=new("Auto,Auto,Auto,*,Auto"),RowSpacing=8 };
        var title=new TextBlock {Text=string.IsNullOrWhiteSpace(mail.Subject)?"（无主题）":mail.Subject,FontSize=22,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap};
        layout.Children.Add(title);
        var headers=new TextBlock{Text=$"发件人　{mail.From}\n收件人　{mail.To}\n日期　　{mail.Date.LocalDateTime:g}",TextWrapping=TextWrapping.Wrap};
        Grid.SetRow(headers,1);layout.Children.Add(headers);
        var footer=new TextBlock{TextWrapping=TextWrapping.Wrap};var foot=new StackPanel{Spacing=4};
        var openLink=new Button{Content="在浏览器中打开",IsVisible=false};Uri? pending=null;
        openLink.Click+=(_,_)=>{if(pending is not null && pending.Scheme is "http" or "https")try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pending.AbsoluteUri){UseShellExecute=true});}catch(Exception e){footer.Text=e.Message;}};
        foot.Children.Add(footer);foot.Children.Add(openLink);Grid.SetRow(foot,4);layout.Children.Add(foot);
        var attachments=(mail.Parts??[]).Where(p=>p.Attachment).ToArray();
        var row=new WrapPanel{IsVisible=attachments.Length>0};
        var files=new ComboBox {ItemsSource=attachments,SelectedIndex=0,MaxWidth=330,
            ItemTemplate=new FuncDataTemplate<MimePartInfo>((p,_)=>new TextBlock{Text=string.IsNullOrWhiteSpace(p?.Name)?"未命名附件":p.Name})};
        AutomationProperties.SetAutomationId(files,"original-attachments");AutomationProperties.SetName(files,"附件");
        row.Children.Add(files);var save=new Button{Content="保存附件…"};row.Children.Add(save);Grid.SetRow(row,2);layout.Children.Add(row);
        save.Click+=async (_,_)=>{
            if(files.SelectedItem is not MimePartInfo part)return;
            save.IsEnabled=false;
            try
            {
                var folder=(await window.StorageProvider.OpenFolderPickerAsync(new(){Title="选择附件保存位置"})).FirstOrDefault()?.TryGetLocalPath();
                if(folder!=null){var saved=await AttachmentService.SaveOriginal(raw,part.Path,folder,cancellation:token);footer.Text="已保存到 "+saved.Path;}
            }
            catch(OperationCanceledException){} catch(Exception e){footer.Text="保存失败："+e.Message;}
            finally{save.IsEnabled=true;}
        };
        var content=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
        Grid.SetRow(content,3);layout.Children.Add(content);
        var plain=new TextBox{Text=mail.Text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap};
        content.Content=plain;SecureHtmlView? html=null;
        if(!string.IsNullOrWhiteSpace(mail.Html))
        {
            var uri="https://mail.mymailbox.invalid/"+Guid.NewGuid().ToString("N")+"/";
            var document=await new HtmlMailRenderer().Render(mail,uri,cancellation:token);
            html=new SecureHtmlView(uri+"index.html",document);
            html.Failed+=message=>{content.Content=plain;footer.Text=message;};
            html.ExternalLink+=link=>{pending=link;footer.Text="邮件中的网址："+link.AbsoluteUri;openLink.IsVisible=link.Scheme is "http" or "https";};
            content.Content=html;
        }
        window.Content=layout;
        try { await window.ShowDialog(owner); }
        finally { html?.Dispose(); }
    }
}
