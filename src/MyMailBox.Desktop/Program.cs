using Avalonia;

namespace MyMailBox.Desktop;

internal static class Program
{
    internal static MyMailBox.Runtime.WorkspaceOwner? Owner {get;private set;}
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--runtime-check")) return;
        int option=Array.IndexOf(args,"--workspace");
        if(option>=0){if(option+1==args.Length||!Path.IsPathFullyQualified(args[option+1]))throw new ArgumentException("缺少工作空间绝对路径。");Environment.SetEnvironmentVariable("MYMAILBOX_HOME",args[option+1]);}
        var home=Environment.GetEnvironmentVariable("MYMAILBOX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MyMailBox");
        using var owner=MyMailBox.Runtime.WorkspaceOwner.Open(home,!args.Contains("--tray"));if(owner is null)return;Owner=owner;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
