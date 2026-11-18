using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Win32;
using MyMailBox.Contracts;
using MyMailBox.Runtime;
using System.Text.Json;

namespace MyMailBox.Desktop;

internal sealed class DesktopLifetime:IDisposable
{
    private sealed record Options(bool CloseToTray=false,bool MinimizeToTray=false);
    private readonly Window _window;private readonly string _home,_path;
    private readonly TrayIcon _icon;private readonly IDisposable _page;
    private Options _settings;
    internal bool ExplicitExit {get;private set;}
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run",StartupValue="MyMailBox";
    internal DesktopLifetime(Window window,Contributions pages,string home)
    {
        _window=window;_home=Path.GetFullPath(home);_path=Path.Combine(_home,"desktop-lifetime.json");
        try{_settings=File.Exists(_path)?JsonSerializer.Deserialize<Options>(File.ReadAllText(_path))??new():new();}
        catch(JsonException){_settings=new();pages.Notice="运行方式设置损坏，已恢复为关闭窗口时退出。";}
        using var bitmap=new RenderTargetBitmap(new PixelSize(32,32));
        using(var draw=bitmap.CreateDrawingContext())
        {
            draw.DrawRectangle(Brushes.MidnightBlue,null,new Rect(0,0,32,32),6,6);
            draw.DrawRectangle(Brushes.White,null,new Rect(5,9,22,15),2,2);
            var pen=new Pen(Brushes.MidnightBlue,2);draw.DrawLine(pen,new Point(5,10),new Point(16,18));draw.DrawLine(pen,new Point(16,18),new Point(27,10));
        }
        var menu=new NativeMenu();var restore=new NativeMenuItem("打开 MyMailBox");restore.Click+=(_,_)=>Restore();menu.Items.Add(restore);
        var quit=new NativeMenuItem("完全退出");quit.Click+=(_,_)=>Exit();menu.Items.Add(quit);
        _icon=new TrayIcon{Icon=new WindowIcon(bitmap),ToolTipText="MyMailBox · 接收和任务继续运行",Menu=menu,IsVisible=true};
        _icon.Clicked+=(_,_)=>Restore();TrayIcon.SetIcons(Application.Current!,new TrayIcons{_icon});
        _window.PropertyChanged+=WindowChanged;Program.Owner?.OnRestore(()=>Dispatcher.UIThread.Post(Restore));
        _page=pages.AddNative(new("desktop/lifetime","运行方式",View,_=>{}));
    }
    private void WindowChanged(object? sender,AvaloniaPropertyChangedEventArgs e)
    {
        if(e.Property==Window.WindowStateProperty&&_settings.MinimizeToTray&&_window.WindowState==WindowState.Minimized)_window.Hide();
    }
    internal bool ShouldHide(WindowClosingEventArgs args)=>!ExplicitExit&&_settings.CloseToTray&&args.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;
    internal void Restore(){_window.Show();if(_window.WindowState==WindowState.Minimized)_window.WindowState=WindowState.Normal;_window.Activate();}
    private void Exit(){ExplicitExit=true;_window.Close();}
    private string StartupCommand()
    {
        var exe=Environment.ProcessPath??throw new InvalidOperationException("无法定位程序。");
        var entry=Path.GetFileNameWithoutExtension(exe).Equals("dotnet",StringComparison.OrdinalIgnoreCase)?" \""+typeof(DesktopLifetime).Assembly.Location+"\"":"";
        return "\""+exe+"\""+entry+" --tray --workspace \""+_home.TrimEnd(Path.DirectorySeparatorChar)+"\"";
    }
    private bool StartsAtLogin(){if(!OperatingSystem.IsWindows())return false;using var key=Registry.CurrentUser.OpenSubKey(RunKey);return Equals(key?.GetValue(StartupValue),StartupCommand());}
    private void SetStartup(bool enabled)
    {
        if(!OperatingSystem.IsWindows()||StartsAtLogin()==enabled)return;
        using var key=Registry.CurrentUser.CreateSubKey(RunKey);
        if(enabled)key.SetValue(StartupValue,StartupCommand(),RegistryValueKind.String);else key.DeleteValue(StartupValue,false);
    }
    private object View()
    {
        var p=new StackPanel{MaxWidth=720,Spacing=10};p.Children.Add(new TextBlock{Text="运行方式",FontSize=28,FontWeight=FontWeight.SemiBold});
        var close=new CheckBox{Content="关闭窗口后继续在托盘运行",IsChecked=_settings.CloseToTray};
        var min=new CheckBox{Content="最小化时隐藏到托盘",IsChecked=_settings.MinimizeToTray};
        var login=new CheckBox{Content="登录 Windows 时启动此工作空间到托盘",IsChecked=StartsAtLogin()};
        AutomationProperties.SetAutomationId(close,"lifetime-close-tray");AutomationProperties.SetAutomationId(min,"lifetime-min-tray");AutomationProperties.SetAutomationId(login,"lifetime-autostart");
        p.Children.Add(close);p.Children.Add(min);p.Children.Add(login);
        p.Children.Add(new TextBlock{Text="在托盘运行时仍会接收邮件和执行已启用的任务。点击托盘图标恢复窗口。完全退出后停止工作，下一次启动按计划设置补跑。",TextWrapping=TextWrapping.Wrap});
        var message=new TextBlock();AutomationProperties.SetAutomationId(message,"lifetime-status");
        var save=new Button{Content="保存运行方式"};AutomationProperties.SetAutomationId(save,"lifetime-save");save.Click+=(_,_)=>{
            try
            {
                var next=new Options(close.IsChecked==true,min.IsChecked==true);string tmp=_path+".new";
                File.WriteAllText(tmp,JsonSerializer.Serialize(next));SetStartup(login.IsChecked==true);File.Move(tmp,_path,true);_settings=next;message.Text="运行方式已保存。";
            }catch(Exception e){message.Text=e.Message;}
        };p.Children.Add(save);
        var hide=new Button{Content="现在隐藏到托盘"};AutomationProperties.SetAutomationId(hide,"lifetime-hide");hide.Click+=(_,_)=>_window.Hide();p.Children.Add(hide);
        var exit=new Button{Content="完全退出 MyMailBox"};AutomationProperties.SetAutomationId(exit,"lifetime-exit");exit.Click+=(_,_)=>Exit();p.Children.Add(exit);p.Children.Add(message);return p;
    }
    public void Dispose(){_page.Dispose();_window.PropertyChanged-=WindowChanged;_icon.IsVisible=false;_icon.Dispose();}
}
