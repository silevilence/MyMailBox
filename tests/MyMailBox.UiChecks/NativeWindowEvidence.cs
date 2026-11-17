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
    private delegate bool EnumWindow(nint handle, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
}
