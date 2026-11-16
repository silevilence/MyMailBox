using CordiSharp;
using CordiSharp.Registry;
using MyMailBox.Contracts;
using PrivateGreeting;

namespace ExamplePlugin;

[Plugin("example")]
[Inject("mailbox.host")]
public sealed class Example : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        if (options.FailInitialization) throw new InvalidOperationException("示例新版本初始化失败");
        var host = context.Get<IPluginHost>("mailbox.host")!;
        var received = 0;
        var ticks = 0;
        context.Effect(() => host.Subscribe(_ => Interlocked.Increment(ref received)));
        context.Effect(() => new Timer(_ => Interlocked.Increment(ref ticks), null, 20, 20));
        context.Effect(() => host.Command("ping", _ => Task.FromResult($"{Words.Message(options.Greeting)}; events={received}; ticks={Volatile.Read(ref ticks)}")));
        context.Effect(() => host.Add(new Page("example", "外部插件示例", "通过共享契约贡献的页面，代码与私有依赖在可回收上下文中运行。", [],
            [new("ping", "调用插件命令")], (_, _) => Task.FromResult(Words.Message(options.Greeting)))));
    }
}
