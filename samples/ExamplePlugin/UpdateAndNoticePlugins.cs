using CordiSharp;
using CordiSharp.Registry;
using MyMailBox.Contracts;

namespace ExamplePlugin;

[Plugin("local-feed")]
[Inject("mailbox.host")]
public sealed class LocalFeedPlugin : IPlugin<PluginOptions>, IUpdateSource
{
    public string Id => "local";
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.AddUpdateSource("离线示例更新源", this));
    }

    public Task<UpdateCheck> Check(string currentVersion, UpdateChannel channel, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new UpdateCheck(UpdateCheckState.Current, "离线示例已检查版本 " + currentVersion, DateTimeOffset.UtcNow));
    }
}

[Plugin("notice")]
[Inject("mailbox.host")]
public sealed class NoticePlugin : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.AddNotificationPresenter(new("banner", "本地摘要通知", (batch, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            host.Notify($"本地摘要通知，共 {batch.Count} 项");
            return Task.CompletedTask;
        })));
    }
}
