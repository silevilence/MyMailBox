using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

/// <summary>Dedicated read-only WebView. No script injection, host objects or editor bridge.</summary>
public sealed class SecureHtmlView(string documentUri, SafeMailDocument document) : NativeControlHost, IDisposable
{
    private nint _window;
    private CoreWebView2Controller? _controller;
    private CoreWebView2Environment? _environment;
    private readonly List<Stream> _streams = [];
    private bool _disposed;
    public event Action<Uri>? ExternalLink;
    public event Action<string>? Failed;
    public event Action? DocumentLoaded;
    public int BlockedRequests { get; private set; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _window = CreateWindowEx(0, "STATIC", "", 0x50000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (_window == 0) throw new InvalidOperationException("无法创建 HTML 阅读窗口。");
        SizeChanged += Resize;
        _ = Initialize();
        return new PlatformHandle(_window, "HWND");
    }

    private async Task Initialize()
    {
        try
        {
            var workspace = Environment.GetEnvironmentVariable("MYMAILBOX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMailBox");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(workspace, "webview-reader"));
            if (_disposed || _window == 0) return;
            _environment = environment;
            var controller = await environment.CreateCoreWebView2ControllerAsync(_window);
            if (_disposed || _window == 0) { controller.Close(); return; }
            _controller = controller;
            var core = controller.CoreWebView2; var settings = core.Settings;
            settings.IsScriptEnabled = false; settings.IsWebMessageEnabled = false; settings.AreHostObjectsAllowed = false;
            settings.AreDefaultScriptDialogsEnabled = false; settings.AreDefaultContextMenusEnabled = false; settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false; settings.IsPasswordAutosaveEnabled = false; settings.IsGeneralAutofillEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += ResourceRequested;
            core.NavigationStarting += NavigationStarting;
            core.NewWindowRequested += (_, args) => { args.Handled = true; if (args.IsUserInitiated) OfferLink(args.Uri); };
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += (_, args) => { if (args.IsSuccess) DocumentLoaded?.Invoke(); else Failed?.Invoke("HTML 加载未完成，可切换纯文本或原文。"); };
            controller.IsVisible = true; Resize(null, null!); core.Navigate(documentUri);
        }
        catch (Exception error) { if (!_disposed) Failed?.Invoke("HTML 阅读不可用，请安装 WebView2 Runtime 或切换纯文本。" + error.GetType().Name); }
    }
    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        byte[] bytes; string type; int status;
        var baseUri = documentUri[..(documentUri.LastIndexOf('/') + 1)];
        var resource = document.Resources.FirstOrDefault(r => args.Request.Uri == baseUri + "resources/" + r.Key);
        if (args.Request.Method == "GET" && args.Request.Uri == documentUri) { bytes = Encoding.UTF8.GetBytes(document.Html); type = "text/html; charset=utf-8"; status = 200; }
        else if (args.Request.Method == "GET" && resource is not null) { bytes = resource.Data; type = resource.ContentType; status = 200; }
        else { bytes = []; type = "text/plain"; status = 403; BlockedRequests++; }
        var stream = new MemoryStream(bytes, false); _streams.Add(stream);
        args.Response = _environment!.CreateWebResourceResponse(stream, status, status == 200 ? "OK" : "Blocked", "Content-Type: " + type + "\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n");
    }
    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri == documentUri) return;
        args.Cancel = true; if (args.IsUserInitiated) OfferLink(args.Uri);
    }
    private void OfferLink(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") ExternalLink?.Invoke(uri);
    }
    private void Resize(object? sender, SizeChangedEventArgs args)
    {
        if (_controller is null || _window == 0) return;
        GetClientRect(_window, out var rect); _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom));
        _controller.NotifyParentWindowPositionChanged();
    }
    public void Zoom(double delta) { if (_controller is not null) _controller.ZoomFactor = Math.Clamp(_controller.ZoomFactor + delta, 0.5, 2.5); }
    public async Task PrintPdf(string path)
    {
        if (_controller is null || _environment is null) throw new InvalidOperationException("HTML 打印尚未就绪。");
        var settings = _environment.CreatePrintSettings(); settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false; settings.PageWidth = 8.27; settings.PageHeight = 11.69;
        settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.4;
        if (!await _controller.CoreWebView2.PrintToPdfAsync(Path.GetFullPath(path), settings)) throw new IOException("PDF 未能写入，请检查目标目录或稍后重试。");
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control) { Dispose(); if (_window != 0) { DestroyWindow(_window); _window = 0; } }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; SizeChanged -= Resize;
        _controller?.Close(); _controller = null; _environment = null;
        foreach (var stream in _streams) stream.Dispose(); _streams.Clear(); ExternalLink = null; Failed = null; DocumentLoaded = null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
}
