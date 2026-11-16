using CordiSharp;
using MyMailBox.Contracts;

namespace MyMailBox.Builtins;

public static class WelcomePlugin
{
    public static void Apply(Context context)
    {
        var pages = context.Get<IContributions>("mailbox.ui")!;
        var version = typeof(WelcomePlugin).Assembly.GetName().Version!;
        context.Effect(() => pages.Add(new Page("welcome", "欢迎", $"MyMailBox {version.ToString(3)}\n你的邮件工作空间，从这里开始。", [], [], (_, _) => Task.FromResult(""))));
    }
}
