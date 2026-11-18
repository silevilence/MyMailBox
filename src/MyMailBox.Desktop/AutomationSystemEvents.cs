using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class AutomationSystemEvents : IDisposable
{
    private readonly Window _window;
    private readonly Func<IMailAutomation?> _service;
    private readonly Action<string> _error;
    private nint _handle;
    private bool _closed;
    internal AutomationSystemEvents(Window window,Func<IMailAutomation?> service,Action<string> error)
    {
        _window=window;_service=service;_error=error;
        window.Opened+=Opened;Win32Properties.AddWndProcHookCallback(window,Message);
        NetworkChange.NetworkAvailabilityChanged+=Availability;
    }
    private void Opened(object? sender,EventArgs e)
    {
        _handle=_window.TryGetPlatformHandle()?.Handle??0;
        if(_handle!=0 && !WTSRegisterSessionNotification(_handle,0))_error("无法注册 Windows 解锁通知。");
        Emit("startup");
    }
    private nint Message(nint hwnd,uint msg,nint wParam,nint lParam,ref bool handled)
    {
        if(msg==0x02B1 && wParam==8)Emit("unlock");
        if(msg==0x0218 && wParam==18)Emit("resume");
        return 0;
    }
    private void Availability(object? sender,NetworkAvailabilityEventArgs e){if(e.IsAvailable)Emit("network");}
    private void Emit(string kind)=>Dispatcher.UIThread.Post(async()=>
    {
        if(!_closed && _service() is {} svc)
            try{await svc.SystemEvent(kind);}catch(ObjectDisposedException){}catch(OperationCanceledException){}catch(Exception e){_error(e.Message);}
    });
    public void Dispose()
    {
        _closed=true;_window.Opened-=Opened;NetworkChange.NetworkAvailabilityChanged-=Availability;
        Win32Properties.RemoveWndProcHookCallback(_window,Message);if(_handle!=0)WTSUnRegisterSessionNotification(_handle);
    }
    [DllImport("wtsapi32.dll")]private static extern bool WTSRegisterSessionNotification(nint hwnd,uint flags);
    [DllImport("wtsapi32.dll")]private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
}
