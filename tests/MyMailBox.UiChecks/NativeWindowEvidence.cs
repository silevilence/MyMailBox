using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;

internal static class NativeWindowEvidence
{
    public static void Write(Window window, string path)
    {
        var lines = new List<string>();
        EnumChildWindows(window.Properties.NativeWindowHandle.Value, (handle, _) =>
        {
            var name = new StringBuilder(256); GetClassName(handle, name, name.Capacity);
            GetClientRect(handle, out var client); GetWindowRect(handle, out var bounds);
            lines.Add($"{name}: client={client.Right}x{client.Bottom}; screen={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}; visible={IsWindowVisible(handle)}");
            return true;
        }, 0);
        File.WriteAllLines(path, lines);
    }
    public static void CaptureClient(Window window, string path)
    {
        var handle = window.Properties.NativeWindowHandle.Value;
        if (!GetClientRect(handle, out var rect)) throw new InvalidOperationException("无法读取客户区大小。");
        using var bitmap = new System.Drawing.Bitmap(rect.Right, rect.Bottom);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var device = graphics.GetHdc();
            try
            {
                if (!PrintWindow(handle, device, 3)) throw new InvalidOperationException("无法保存窗口画面。");
            }
            finally { graphics.ReleaseHdc(device); }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint handle,nint device,uint flags);
    private delegate bool EnumWindow(nint handle, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
}
