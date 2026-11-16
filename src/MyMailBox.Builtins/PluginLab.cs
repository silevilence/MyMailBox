using CordiSharp;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public static class PluginLab
{
    public static void Apply(Context context, PluginRuntime runtime)
    {
        var ui = context.Get<IContributions>("mailbox.ui")!;
        context.Effect(() => ui.Add(new Page("plugin-lab", "插件验证", "装入外部示例、调用命令并释放。此页用于 SDK 集成验证。", [new("assembly-directory", "ExamplePlugin.dll 所在目录")],
            [new("load-example", "启用示例"), new("call-example", "调用命令"), new("unload-example", "停用示例")], async (values, action) =>
            {
                if (action == "load-example")
                {
                    await runtime.Start(new(new("example", "1.0.0", 1, 1, PluginKind.Ordinary, "example", "ExamplePlugin.dll"), PluginOrigin.External,
                        values["assembly-directory"]), new() { InstanceId = "lab" });
                    return "示例已启用，侧栏中可打开外部页面。";
                }
                if (action == "call-example") { runtime.Events.Emit("ui"); return await runtime.Scope("lab").Execute("ping"); }
                await runtime.Stop("lab");
                return "示例已停用，事件、页面与程序集已释放。";
            })));
    }
}
