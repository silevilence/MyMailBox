using CordiSharp;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public static class DefaultProfile
{
    public static PluginConfiguration Core => new(1, [new("platform", "core.platform", "1.0.0")]);
    public static PluginConfiguration Ordinary => new(1, [new("welcome", "welcome", "1.0.0")]);

    public static PluginCatalog Catalog()
    {
        var catalog = new PluginCatalog();
        catalog.Add(new(new("core.platform", "1.0.0", 1, 1, PluginKind.Core, "platform"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var host = ctx.Get<IPluginHost>("mailbox.host")!;
            ctx.Effect(() => host.Command("environment", _ => Task.FromResult("Windows x64 / .NET " + Environment.Version)));
        }));
        catalog.Add(new(new("welcome", "1.0.0", 1, 1, PluginKind.Ordinary, "welcome"), PluginOrigin.Builtin, "", (ctx, _) =>
        {
            var host = ctx.Get<IPluginHost>("mailbox.host")!;
            ctx.Effect(() => host.Add(new Page("welcome", "欢迎", "MyMailBox 0.1.0\n你的邮件工作空间，从这里开始。", [], [], (_, _) => Task.FromResult(""))));
        }));
        return catalog;
    }

    public static void Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!File.Exists(Path.Combine(directory, "core.json"))) ConfigurationFiles.Save(Path.Combine(directory, "core.json"), Core);
        if (!File.Exists(Path.Combine(directory, "plugins.json"))) ConfigurationFiles.Save(Path.Combine(directory, "plugins.json"), Ordinary);
    }

    public static IDisposable Recovery(Contributions pages, string error, string directory) => pages.Add(new Page("recovery", "启动修复", error,
        [], [new("open-profile", "打开配置目录"), new("safe-default", "备份并恢复默认核心配置")], (_, action) =>
        {
            if (action == "open-profile") System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            else
            {
                var path = Path.Combine(directory, "core.json");
                if (File.Exists(path)) File.Copy(path, path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                ConfigurationFiles.Save(path, Core);
            }
            return Task.FromResult("修复后请关闭并重新启动应用。当前安全模式保留。 ");
        }));
}
