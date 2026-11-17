using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

public sealed class WebViewPdfPrinter : IMailPdfPrinter
{
    public async Task Print(string documentUri, SafeMailDocument document, string path, CancellationToken cancellation = default)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var view = new SecureHtmlView(documentUri, document);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.DocumentLoaded += () => loaded.TrySetResult(); view.Failed += error => loaded.TrySetException(new IOException(error));
            var window = new Window { Width = 794, Height = 1123, ShowInTaskbar = false, ShowActivated = false, CanResize = false,
                WindowDecorations = WindowDecorations.None, Position = new PixelPoint(-32000, -32000), Content = view };
            try
            {
                window.Show(); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
                cancellation.ThrowIfCancellationRequested();
                // WebView2 cannot cancel a print in progress. Drain this file before cleaning up.
                await view.PrintPdf(path); cancellation.ThrowIfCancellationRequested();
            }
            finally { window.Content = null; window.Close(); }
        });
    }
}
