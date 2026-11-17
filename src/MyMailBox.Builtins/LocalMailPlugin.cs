using CordiSharp;
using MyMailBox.Contracts;

namespace MyMailBox.Builtins;

public static class LocalMailPlugin
{
    public static void Apply(Context context, IMailStore store, IMimeReader parser, IRemoteContentService? remote = null)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.AddExporter(new Mail.EmlExporter(store)));
        context.Effect(() => host.Add(new Page("inbox", "本地邮箱", "导入 EML 并在此工作空间持久保存。", [], [],
            (_, _) => Task.FromResult(""), new(store, parser, new Mail.HtmlMailRenderer(), remote))));
    }
}
