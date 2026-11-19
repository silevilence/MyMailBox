using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class ExchangeSettingsView : UserControl,IDisposable
{
    private readonly CancellationTokenSource _stop=new();
    private readonly StackPanel _form=new(){Spacing=8,MaxWidth=760};
    private readonly ComboBox _accounts=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly TextBox _endpoint=new(),_user=new(),_password=new(){PasswordChar='●'};
    private readonly CheckBox _device=new(){Content="允许本机作为同步设备读取此邮箱"},_write=new(){Content="允许发信、标记及移动"};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private Guid Account=>(_accounts.SelectedItem as MailAccount)?.Id??throw new InvalidDataException("请先在账户管理添加公司 Exchange（ActiveSync）账户。");
    internal ExchangeSettingsView(ExchangePage page)
    {
        _form.Children.Add(new TextBlock{Text="Exchange 连接",FontSize=28});
        _accounts.ItemTemplate=new FuncDataTemplate<MailAccount>((a,_)=>new TextBlock{Text=a?.Name+" · "+a?.Address});
        Field("账户",_accounts,"account");Field("管理员提供的 HTTPS 服务器或 ActiveSync 地址",_endpoint,"endpoint");
        Field("登录身份",_user,"user");Field("密码（保留现有凭据时留空）",_password,"password");
        _form.Children.Add(new TextBlock{Text="检查能力只查询服务器支持的版本和命令。勾选设备同步后，读取目录可能在服务器上建立设备记录。遇到组织设备策略时会显示管理员处理提示。",TextWrapping=TextWrapping.Wrap});
        Id(_device,"device");Id(_write,"write");_form.Children.Add(_device);_form.Children.Add(_write);
        _device.IsCheckedChanged+=(_,_)=>{_write.IsEnabled=_device.IsChecked==true;if(!_write.IsEnabled)_write.IsChecked=false;};
        Add("保存连接设置","save",async()=>
        {
            var password=_password.Text;_password.Text="";
            await page.Service.Configure(Account,_endpoint.Text??"",_user.Text??"",password,_device.IsChecked==true,_write.IsChecked==true,_stop.Token);
            _status.Text="设置已保存。可检查服务器能力。";
        });
        Add("检查服务器能力","check",async()=>
        {
            var s=await page.Service.Check(Account,_stop.Token);
            _status.Text=$"ActiveSync {s.Version} · {string.Join("、",s.Commands)}。"+(s.DeviceAllowed?"可在“邮箱浏览与整理”连接及读取目录。":"启用设备同步后可读取邮箱。");
        });
        Id(_status,"status");Content=PageFeedback.Layout(_form,_status);
        _accounts.SelectionChanged+=async(_,_)=>await Run(async()=>
        {
            _password.Text="";_endpoint.Text="";_user.Text="";_device.IsChecked=false;_write.IsChecked=false;
            var id=Account;var s=await page.Service.Settings(id,_stop.Token);
            if((_accounts.SelectedItem as MailAccount)?.Id!=id)return;
            _endpoint.Text=s.Endpoint;_user.Text=s.User;_device.IsChecked=s.DeviceAllowed;_write.IsEnabled=s.DeviceAllowed;_write.IsChecked=s.WriteAllowed;
            _status.Text=s.Version.Length!=0?"已检查 ActiveSync "+s.Version+"。":"请配置连接并检查能力。";
        });
        AttachedToVisualTree+=async(_,_)=>await Run(async()=>
        {
            _accounts.ItemsSource=(await page.Accounts.List(_stop.Token)).Where(a=>a.Provider=="exchange-eas" && a.Enabled).ToArray();_accounts.SelectedIndex=0;
            if(_accounts.SelectedIndex<0)_status.Text="请先在账户管理添加公司 Exchange（ActiveSync）账户。";
        });
    }
    private static void Id(Control c,string id)=>AutomationProperties.SetAutomationId(c,"exchange-"+id);
    private void Field(string title,Control c,string id){_form.Children.Add(new TextBlock{Text=title});Id(c,id);AutomationProperties.SetName(c,title);_form.Children.Add(c);}
    private void Add(string title,string id,Func<Task> work)
    {
        var b=new Button{Content=title};Id(b,id);b.Click+=async(_,_)=>{_form.IsEnabled=false;try{await Run(work);}finally{_form.IsEnabled=true;}};_form.Children.Add(b);
    }
    private async Task Run(Func<Task> work)
    {
        try{await work();}catch(OperationCanceledException){}catch(Exception e){_status.Text=e is InvalidDataException or InvalidOperationException?e.Message:"连接未完成，请检查服务器地址、证书与网络。";}
    }
    public void Dispose(){_password.Text="";_stop.Cancel();_stop.Dispose();}
}
