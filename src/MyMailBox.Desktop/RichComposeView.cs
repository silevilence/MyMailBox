using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MyMailBox.Desktop;

internal sealed class RichComposeView(DraftBody initial,Action<DraftBody> changed,Action<string> failure,Action loaded,Func<string,(string Mime,byte[] Data)?> inline) : NativeControlHost,IDisposable
{
    private readonly string _uri="https://compose.mymailbox.invalid/"+Guid.NewGuid().ToString("N")+"/editor";
    private nint _window;
    private CoreWebView2Controller? _controller;
    private MemoryStream? _document;
    private readonly List<MemoryStream> _images=[];
    private bool _disposed,_ready;
    private const string Page="""
        <!doctype html><html lang="zh-CN"><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-NONCE'; style-src 'unsafe-inline'; img-src 'self'; form-action 'none'; base-uri 'none'">
        <style>body{font:16px 'Segoe UI',sans-serif;margin:8px}button{margin-right:4px;padding:4px 8px}#body{padding:8px;min-height:112px;border:1px solid #888;margin-top:8px}button:focus-visible,#body:focus-visible{outline:2px solid #075985}</style>
        <div role="toolbar" aria-label="正文格式"><button data-c="bold">加粗</button><button data-c="italic">斜体</button><button data-c="underline">下划线</button><button data-c="insertUnorderedList">列表</button><button data-c="undo">撤销</button><button data-c="redo">重做</button></div>
        <div id="body" role="textbox" aria-label="富文本正文" aria-multiline="true" contenteditable="true" spellcheck="true"></div>
        <script nonce="NONCE">
        const body=document.querySelector('#body');
        function snapshot(){const copy=body.cloneNode(true);for(const img of copy.querySelectorAll('img[data-cid]')){img.setAttribute('src','cid:'+img.dataset.cid);img.removeAttribute('data-cid')}return {kind:'change',plain:body.innerText,html:copy.innerHTML}}
        function send(){chrome.webview.postMessage(snapshot())}
        body.addEventListener('input',send);
        body.addEventListener('paste',e=>{e.preventDefault();document.execCommand('insertText',false,e.clipboardData.getData('text/plain'))});
        body.addEventListener('drop',e=>e.preventDefault());
        for(const b of document.querySelectorAll('button')){b.onmousedown=e=>e.preventDefault();b.onclick=()=>{body.focus();document.execCommand(b.dataset.c);send()}}
        chrome.webview.addEventListener('message',e=>{if(e.data.kind==='load'){body.innerHTML=e.data.html;for(const img of body.querySelectorAll('img')){const src=img.getAttribute('src')||'';if(src.startsWith('cid:')){img.dataset.cid=src.slice(4);img.src=location.href+'/inline/'+encodeURIComponent(img.dataset.cid)}}chrome.webview.postMessage({kind:'loaded'})}});
        chrome.webview.postMessage({kind:'ready'});
        </script></html>
        """;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _window=CreateWindowEx(0,"STATIC","",0x50000000,0,0,1,1,parent.Handle,0,0,0);
        if(_window==0)throw new InvalidOperationException("无法创建富文本编辑窗口。");
        SizeChanged+=Resize;_ = Initialize();return new PlatformHandle(_window,"HWND");
    }

    private async Task Initialize()
    {
        try
        {
            var workspace=Environment.GetEnvironmentVariable("MYMAILBOX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MyMailBox");
            var env=await CoreWebView2Environment.CreateAsync(userDataFolder:Path.Combine(workspace,"webview-composer"));
            if(_disposed)return;var controller=await env.CreateCoreWebView2ControllerAsync(_window);
            if(_disposed){controller.Close();return;}_controller=controller;
            var core=controller.CoreWebView2;var settings=core.Settings;settings.IsScriptEnabled=true;settings.IsWebMessageEnabled=true;settings.AreHostObjectsAllowed=false;
            settings.AreDevToolsEnabled=false;settings.IsGeneralAutofillEnabled=false;settings.IsPasswordAutosaveEnabled=false;settings.AreDefaultScriptDialogsEnabled=false;
            var page=Encoding.UTF8.GetBytes(Page.Replace("NONCE",Guid.NewGuid().ToString("N"),StringComparison.Ordinal));
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(_,e)=>
            {
                if(e.Request.Uri==_uri && e.Request.Method=="GET")
                { _document=new MemoryStream(page,false);e.Response=env.CreateWebResourceResponse(_document,200,"OK","Content-Type: text/html; charset=utf-8"); }
                else if(e.Request.Method=="GET" && e.Request.Uri.StartsWith(_uri+"/inline/",StringComparison.Ordinal) &&
                    inline(Uri.UnescapeDataString(e.Request.Uri[(_uri.Length+8)..])) is {} image)
                { var stream=new MemoryStream(image.Data,false);_images.Add(stream);e.Response=env.CreateWebResourceResponse(stream,200,"OK","Content-Type: "+image.Mime); }
                else e.Response=env.CreateWebResourceResponse(null,403,"Blocked","");
            };
            core.NavigationStarting+=(_,e)=>e.Cancel=e.Uri!=_uri;
            core.NewWindowRequested+=(_,e)=>e.Handled=true;core.DownloadStarting+=(_,e)=>{e.Cancel=true;e.Handled=true;};
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.WebMessageReceived+=(_,e)=>
            {
                if(_disposed || e.Source!=_uri)return;
                try
                {
                    using var value=JsonDocument.Parse(e.WebMessageAsJson);
                    switch(value.RootElement.GetProperty("kind").GetString())
                    {
                        case "ready":core.PostWebMessageAsJson(JsonSerializer.Serialize(new{kind="load",html=MailDraftStore.SafeMarkup(initial.Html)}));break;
                        case "loaded":_ready=true;loaded();break;
                        case "change" when _ready:Accept(value.RootElement);break;
                    }
                }
                catch(Exception error){failure("编辑内容尚未保存："+error.Message);}
            };
            Resize(null,null!);controller.IsVisible=true;core.Navigate(_uri);
        }
        catch(Exception e){if(!_disposed)failure("富文本编辑器无法打开，可切换纯文本继续。"+e.GetType().Name);}
    }
    private DraftBody Read(JsonElement root)
    {
        string plain=root.GetProperty("plain").GetString() ?? "",html=root.GetProperty("html").GetString() ?? "";
        return new(plain,MailDraftStore.SafeMarkup(html),plain,true);
    }
    private void Accept(JsonElement value)=>changed(Read(value));
    public async Task Flush()
    {
        if(!_ready || _controller is null || _disposed)return;
        string json=await _controller.CoreWebView2.ExecuteScriptAsync("snapshot()");
        if(!_disposed){using var value=JsonDocument.Parse(json);Accept(value.RootElement);}
    }
    private void Resize(object? sender,SizeChangedEventArgs args)
    {
        if(_controller is null || _window==0)return;GetClientRect(_window,out var r);
        _controller.Bounds=new(0,0,Math.Max(1,r.Right),Math.Max(1,r.Bottom));_controller.NotifyParentWindowPositionChanged();
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {Dispose();if(_window!=0){DestroyWindow(_window);_window=0;}}
    public void Dispose(){if(_disposed)return;_disposed=true;SizeChanged-=Resize;_controller?.Close();_controller=null;_document?.Dispose();foreach(var img in _images)img.Dispose();_images.Clear();}
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CreateWindowEx(int ex,string cls,string name,uint style,int x,int y,int w,int h,nint parent,nint menu,nint module,nint arg);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd,out Rect rect);
}
