using CordiSharp;
using MyMailBox.Contracts;

namespace MyMailBox.Builtins;

public static class WelcomePlugin
{
    public static void Apply(Context context)
    {
        var pages = context.Get<IContributions>("mailbox.ui")!;
        context.Effect(() => pages.Add(UserGuide.Welcome()));
    }
}
