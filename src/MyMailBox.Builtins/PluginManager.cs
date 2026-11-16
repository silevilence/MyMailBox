using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public static class PluginManager
{
    public static IDisposable Register(Contributions pages, PluginPackages packages, PluginCatalog catalog, HotConfiguration config, string corePath, PluginRuntime runtime)
        => pages.Add(new Page("plugin-manager", "插件管理", "先预览插件的来源、版本、依赖和权限，再安装。仅运行你信任的同进程插件；包校验和用于检测损坏，不证明发布者身份。",
            [new("package-path", "插件包（ZIP）", FilePicker: true), new("plugin-id", "插件 ID", "example"), new("plugin-version", "版本", "1.0.0"),
             new("managed-instance", "实例 ID", "example"), new("grants", "授予权限（逗号分隔；预览后填写）"), new("fail-initialization", "示例升级故障注入", "no", [new("no", "正常初始化"), new("yes", "模拟初始化失败")])],
            [new("inspect-package", "预览插件包"), new("install-package", "安装插件包"), new("enable-plugin", "启用实例"), new("disable-plugin", "停用实例"),
             new("upgrade-plugin", "更新到指定版本"), new("start-operation", "启动模拟在途操作"), new("remove-instance", "移除实例配置"), new("remove-package", "移除版本文件"), new("list-plugins", "查看插件状态")], async (values, action) =>
            {
                if (action == "inspect-package")
                {
                    var manifest = packages.Inspect(values["package-path"]);
                    return Describe(manifest) + "\n完整性与 SDK 检查通过。安装只保存文件，不自动启用或授予权限。";
                }
                if (action == "install-package") return Describe(packages.Install(values["package-path"]).Manifest) + "\n已安装到独立版本目录，可继续启用。";
                if (action == "list-plugins") return string.Join("\n", catalog.All.Select(p => Describe(p.Manifest))) + "\n\n实例：\n" + string.Join("\n", config.Current.Instances.Select(p => $"{p.Id} → {p.Plugin}@{p.Version} · {(p.Enabled ? "启用" : "停用/等待依赖")}")) + "\n" + string.Join("\n", runtime.States.Select(p => p.Key + "：" + p.Value));
                var instance = new PluginInstance(values["managed-instance"], values["plugin-id"], values["plugin-version"], Grants: values["grants"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                var definition = catalog.Find(instance);
                if (action == "start-operation")
                {
                    var operation = runtime.Scope(instance.Id).Execute("work");
                    _ = Observe(operation, pages);
                    return "模拟任务已接单。现在更新插件将等待它完成，再切换版本。";
                }
                if (action == "remove-package")
                {
                    if (runtime.Uses(instance.Plugin, instance.Version)) return "此版本仍在运行，不能移除。";
                    packages.Remove(definition, config.Current, ConfigurationFiles.Read(corePath)); return "指定版本已移除，其他版本与用户数据保留。";
                }
                if (definition.Manifest.Kind == PluginKind.Core)
                {
                    if (action != "upgrade-plugin") return "核心插件请编辑 core.json；更新在下次启动生效。";
                    var core = ConfigurationFiles.Read(corePath);
                    if (!core.Instances.Any(p => p.Id == instance.Id)) return "核心配置中没有此实例。";
                    var next = core with { Instances = core.Instances.Select(p => p.Id == instance.Id ? p with { Version = instance.Version } : p).ToArray() };
                    catalog.Order(next, PluginKind.Core); ConfigurationFiles.Save(corePath, next);
                    return "核心版本已保存，下次启动生效；当前实例继续运行。";
                }
                var current = config.Current;
                if (action == "upgrade-plugin")
                {
                    if (!current.Instances.Any(p => p.Id == instance.Id && p.Plugin == instance.Plugin)) return "找不到同一插件的现有实例。";
                    await config.Save(current with { Instances = current.Instances.Select(p => p.Id != instance.Id ? p : p with
                    { Version = instance.Version, Options = new() { Greeting = p.Options?.Greeting ?? "你好", FailInitialization = values["fail-initialization"] == "yes" } }).ToArray() });
                    return "在途操作已排空，新版本已启用。";
                }
                if (action == "enable-plugin")
                {
                    var next = current with { Instances = current.Instances.Where(p => p.Id != instance.Id).Append(instance).ToArray() };
                    try { await config.Save(next); }
                    catch
                    {
                        if (!current.Instances.Any(p => p.Id == instance.Id)) await config.Save(current with { Instances = current.Instances.Append(instance with { Enabled = false }).ToArray() });
                        throw;
                    }
                    return "实例已启用。";
                }
                var affected = new HashSet<string> { instance.Plugin };
                bool added;
                do
                {
                    added = false;
                    foreach (var item in current.Instances.Where(p => p.Enabled))
                        if ((catalog.Find(item).Manifest.Dependencies ?? []).Any(affected.Contains)) added |= affected.Add(item.Plugin);
                } while (added);
                var items = current.Instances.Select(p => affected.Contains(p.Plugin) ? p with { Enabled = false } : p);
                if (action == "remove-instance") items = items.Where(p => p.Id != instance.Id);
                await config.Save(current with { Instances = items.ToArray() });
                return "已停用相关能力并保留其配置：" + string.Join("、", affected);
            }));

    private static string Describe(PluginManifest m) => $"{m.Id}@{m.Version} · {m.Kind} · 来源：{m.Source}\n依赖：{string.Join(", ", m.Dependencies ?? [])}；权限：{string.Join(", ", m.Permissions ?? [])}";
    private static async Task Observe(Task<string> operation, Contributions pages)
    {
        try { pages.Notice = await operation; }
        catch (Exception error) { pages.Notice = "模拟任务未完成：" + error.Message; }
    }
}
