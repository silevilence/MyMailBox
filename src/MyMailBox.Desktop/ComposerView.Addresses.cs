using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class ComposerView
{
    private readonly ComboBox _suggestSource=new();private readonly ListBox _suggestions=new(){MaxHeight=114,IsVisible=false};
    private readonly TextBox _contactName=new(),_contactAddress=new();private readonly TextBlock _addressStatus=new();
    private TextBox? _recipient;private CancellationTokenSource? _suggesting;

    private static int LastAddressStart(string text)
    {
        bool quoted=false,escaped=false;int last=0;
        for(int i=0;i<text.Length;i++)
        {
            if(escaped){escaped=false;continue;}
            if(quoted&&text[i]=='\\'){escaped=true;continue;}
            if(text[i]=='"')quoted=!quoted;
            if(!quoted&&text[i]==',')last=i+1;
        }
        return last;
    }
    private void AddAddresses()
    {
        _suggestions.ItemTemplate=new FuncDataTemplate<AddressChoice>((c,_)=>new TextBlock{Text=c is null?"":$"{c.Name}  <{c.Address}>"});
        AutomationProperties.SetName(_suggestions,"地址建议");AutomationProperties.SetAutomationId(_suggestions,"compose-suggestions");_form.Children.Add(_suggestions);
        _suggestions.DoubleTapped+=(_,_)=>UseAddress();_suggestions.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){UseAddress();e.Handled=true;}};
        foreach(var field in new[]{_to,_cc,_bcc})
        {
            field.GotFocus+=async(_,_)=>{_recipient=field;if(!_binding)await Suggest();};
            field.TextChanged+=async(_,_)=>{if(!_binding && field==_recipient)await Suggest();};
            field.KeyDown+=(_,e)=>{if(e.Key==Key.Down && _suggestions.IsVisible){_suggestions.Focus();_suggestions.SelectedIndex=0;e.Handled=true;}};
        }
        var settings=new StackPanel{Spacing=5};_suggestSource.ItemTemplate=new FuncDataTemplate<AddressProvider>((p,_)=>new TextBlock{Text=p?.Name});
        settings.Children.Add(new TextBlock{Text="建议来源"});settings.Children.Add(_suggestSource);
        _suggestSource.SelectionChanged+=async(_,_)=>{if(!_binding)await Suggest(true);};
        var buttons=new WrapPanel();Button(buttons,"查看建议","compose-show-addresses",()=>Suggest(true));Button(buttons,"使用所选地址","compose-use-address",()=>{UseAddress();return Task.CompletedTask;});
        Button(buttons,"检查地址并去重","compose-normalize",()=>{
            var result=_page.Drafts.Normalize(new(_to.Text ?? "",_cc.Text ?? "",_bcc.Text ?? ""));
            _to.Text=result.To;_cc.Text=result.Cc;_bcc.Text=result.Bcc;Capture();_addressStatus.Text=$"地址有效，已移除重复地址 {result.Duplicates} 个。";return Task.CompletedTask;});
        settings.Children.Add(buttons);settings.Children.Add(new TextBlock{Text="联系人姓名"});settings.Children.Add(_contactName);
        settings.Children.Add(new TextBlock{Text="邮箱地址"});settings.Children.Add(_contactAddress);
        _suggestions.SelectionChanged+=(_,_)=>{if(_suggestions.SelectedItem is AddressChoice c){_contactName.Text=c.Name;_contactAddress.Text=c.Address;}};
        var manage=new WrapPanel();Button(manage,"保存本地联系人","compose-contact",async()=>{
            _page.Drafts.Contact(Account().Id,new(_contactName.Text ?? "",_contactAddress.Text ?? ""));await Suggest(true);});
        Button(manage,"移除本地建议","compose-forget",async()=>{
            if(_suggestions.SelectedItem is AddressChoice c){_page.Drafts.Forget(Account().Id,c.Address);await Suggest(true);}});
        Button(manage,"清空本账户历史","compose-forget-history",async()=>{_page.Drafts.Forget(Account().Id,null);await Suggest(true);});
        settings.Children.Add(manage);_form.Children.Add(new Expander{Header="地址建议与本地联系人",Content=settings});_form.Children.Add(_addressStatus);
    }
    private void SetAddressProviders()
    {
        var id=(_suggestSource.SelectedItem as AddressProvider)?.Id;
        var local=new AddressProvider("builtin","本地联系人与发送历史",_page.Drafts.Suggest);
        var all=new[]{local}.Concat(_contributions.AddressProviders).ToArray();_suggestSource.ItemsSource=all;_suggestSource.SelectedItem=all.FirstOrDefault(p=>p.Id==id) ?? local;
    }
    private async Task Suggest(bool all=false)
    {
        _suggesting?.Cancel();if(_disposed || _accounts.SelectedItem is not MailAccount account || _suggestSource.SelectedItem is not AddressProvider provider)return;
        var field=_recipient ?? _to;string current=field.Text ?? "";string prefix=all?"":current[LastAddressStart(current)..].Trim();
        if(!all && prefix.Length==0){_suggestions.IsVisible=false;return;}
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);_suggesting=stop;
        try
        {
            if(!all)await Task.Delay(100,stop.Token);
            var rows=await provider.Suggest(account.Id,prefix,stop.Token);
            if(!stop.IsCancellationRequested){_suggestions.ItemsSource=rows.Take(20).ToArray();_suggestions.IsVisible=rows.Count>0;_addressStatus.Text="";}
        }
        catch(OperationCanceledException){}
        catch(Exception e){if(!stop.IsCancellationRequested){_suggestions.IsVisible=false;_addressStatus.Text="建议不可用，可继续手动输入。"+e.Message;}}
        finally{if(ReferenceEquals(_suggesting,stop))_suggesting=null;}
    }
    private void UseAddress()
    {
        if(_suggestions.SelectedItem is not AddressChoice choice)return;
        var field=_recipient ?? _to;string text=field.Text ?? "";field.Text=text[..LastAddressStart(text)]+(LastAddressStart(text)>0?" ":"")+new MailboxAddress(choice.Name,choice.Address)+", ";
        field.CaretIndex=field.Text.Length;field.Focus();_suggestions.IsVisible=false;Capture();
    }
}
