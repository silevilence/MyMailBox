using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed partial class PluginScope
{
    public IReadOnlyList<(Choice Choice, IUpdateSource Source)> UpdateSources => builtin ? pages.UpdateSources : [];
    public MailReader? MailBackend => builtin ? pages.MailBackend : null;

    public IDisposable AddUpdateSource(string name, IUpdateSource source)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(source.Id, @"\A[a-z][a-z0-9-]{0,63}\z"))
            throw new ArgumentException("更新源标识无效。");
        return Own(pages.AddUpdateSource(name, new ScopedSource(this, InstanceId + "/" + source.Id, Target(source))));
    }

    private sealed class ScopedSource(PluginScope owner, string id, ServiceTarget<IUpdateSource> source) : IUpdateSource
    {
        public string Id => id;
        public Task<UpdateCheck> Check(string currentVersion, UpdateChannel channel, CancellationToken cancellation = default) =>
            owner.Run(t => { owner.RequireSourceGrant(); return source.Value.Check(currentVersion, channel, t); }, cancellation);
    }

    private void RequireSourceGrant() => permissions.Require("updates.check");

    public IDisposable AddMailBackend(IMailStore store, IMimeReader parser)
    {
        if (!core) throw new InvalidOperationException("邮件存储仅可由启动时加载的核心插件注册。");
        permissions.Require("mail.storage");
        return Own(pages.AddMailBackend(new ScopedMailStore(this, Target(store)), new ScopedMimeReader(this, Target(parser))));
    }
}
