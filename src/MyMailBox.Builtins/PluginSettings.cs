using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public static class PluginSettings
{
    public static IDisposable Register(Contributions pages, HotConfiguration configuration)
    {
        void Changed() => pages.Notice = configuration.Error is null ? "插件配置已生效" : "插件配置未生效：" + configuration.Error;
        configuration.Changed += Changed;
        var registration = pages.Add(new Page("plugin-settings", "插件设置", "普通插件的更改立即生效。配置错误时继续使用上次有效状态。核心配置修改需要重启。",
            [new("instance-id", "实例 ID", "welcome"), new("instance-enabled", "启用状态", "yes", [new("yes", "启用"), new("no", "停用")]), new("greeting", "示例问候语", "你好")],
            [new("save-plugin-settings", "保存设置"), new("retry-config", "重试文件配置"), new("show-config", "查看当前状态")], async (values, action) =>
            {
                if (action == "retry-config") return await configuration.Reload() ? "文件配置已应用" : configuration.Error!;
                if (action == "show-config") return string.Join("\n", configuration.Current.Instances.Select(p => $"{p.Id} · {p.Plugin}@{p.Version} · {(p.Enabled ? "启用" : "停用")}"));
                var current = configuration.Current;
                if (!current.Instances.Any(p => p.Id == values["instance-id"])) return "找不到实例，请先查看当前状态。";
                var items = current.Instances.Select(p => p.Id != values["instance-id"] ? p : p with
                {
                    Enabled = values["instance-enabled"] == "yes",
                    Options = new PluginOptions { Greeting = values["greeting"] }
                }).ToArray();
                await configuration.Save(current with { Instances = items });
                return "设置已保存并生效。";
            }));
        return new Cleanup(() => { configuration.Changed -= Changed; registration.Dispose(); });
    }
}
