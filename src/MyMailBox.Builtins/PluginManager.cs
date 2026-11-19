using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public static class PluginManager
{
    public static IDisposable Register(Contributions pages, PluginPackages packages, PluginCatalog catalog, HotConfiguration config, string corePath, PluginRuntime runtime)
    {
        var coreSnapshot=ConfigurationFiles.Read(corePath);
        return pages.Add(new Page("plugin-manager", "插件管理", "先预览插件的来源、版本、依赖和权限，再安装。仅运行你信任的同进程插件；包校验和用于检测损坏，不证明发布者身份。",
            [new("package-path", "插件包（ZIP）", FilePicker: true), new("plugin-id", "插件 ID", "example"), new("plugin-version", "版本", "1.0.0"),
             new("managed-instance", "实例 ID", "example"), new("grants", "授予权限（逗号分隔；预览后填写）"), new("fail-initialization", "示例升级故障注入", "no", [new("no", "正常初始化"), new("yes", "模拟初始化失败")])],
            [new("inspect-package", "预览插件包"), new("install-package", "安装插件包"), new("enable-plugin", "启用实例"), new("disable-plugin", "停用实例"),
             new("upgrade-plugin", "更新到指定版本"), new("start-operation", "启动模拟在途操作"), new("remove-instance", "移除实例配置"), new("remove-package", "移除版本文件"), new("list-plugins", "查看插件状态")], async (values, action) =>
            {
                if (action == "retry-config") return await config.Reload() ? "已重新读取插件设置。" : config.Error!;
                if (action == "inspect-package")
                {
                    var manifest = packages.Inspect(values["package-path"]);
                    return Describe(manifest) + "\n插件包已通过完整性和兼容性检查。安装后可在列表中启用。";
                }
                if (action == "install-package") return Describe(packages.Install(values["package-path"]).Manifest) + "\n已安装。请在插件列表中选择并启用。";
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
                    if (runtime.Uses(instance.Plugin, instance.Version)) return runtime.NativeNeedsRestart(instance.Id)
                        ? "插件已经停用。请重新打开软件后再卸载此版本。" : "此版本仍在运行，不能移除。";
                    packages.Remove(definition, config.Current, ConfigurationFiles.Read(corePath)); return "指定版本已移除，其他版本与用户数据保留。";
                }
                if (definition.Manifest.Kind == PluginKind.Core)
                {
                    if (action != "upgrade-plugin") return "核心插件请编辑 core.json；更新在下次启动生效。";
                    var core = ConfigurationFiles.Read(corePath);
                    if (!core.Instances.Any(p => p.Id == instance.Id)) return "核心配置中没有此实例。";
                    var next = core with { Instances = core.Instances.Select(p => p.Id == instance.Id ? p with { Version = instance.Version } : p).ToArray() };
                    catalog.Order(next, PluginKind.Core); ConfigurationFiles.Save(corePath, next); coreSnapshot=next;
                    return "核心版本已保存，下次启动生效；当前实例继续运行。";
                }
                var current = config.Current;
                if (action == "upgrade-plugin")
                {
                    if (!current.Instances.Any(p => p.Id == instance.Id && p.Plugin == instance.Plugin)) return "找不到同一插件的现有实例。";
                    await config.Save(current with { Instances = current.Instances.Select(p => p.Id != instance.Id ? p : p with
                    { Version = instance.Version, Grants = instance.Grants, Options = new() { Greeting = p.Options?.Greeting ?? "你好", FailInitialization = values["fail-initialization"] == "yes" } }).ToArray() });
                    return "插件版本已切换。";
                }
                if (action == "enable-plugin")
                {
                    var existing = current.Instances.FirstOrDefault(p => p.Id == instance.Id);
                    if (existing is not null && existing.Plugin != instance.Plugin) throw new InvalidOperationException("此名称已被其他插件使用。");
                    if (existing is not null) instance = existing with { Enabled = true, Version = instance.Version, Grants = instance.Grants };
                    var next = current with { Instances = existing is null ? [..current.Instances, instance] : current.Instances.Select(p => p.Id == instance.Id ? instance : p).ToArray() };
                    try { await config.Save(next); }
                    catch
                    {
                        if (!current.Instances.Any(p => p.Id == instance.Id)) await config.Save(current with { Instances = current.Instances.Append(instance with { Enabled = false }).ToArray() });
                        throw;
                    }
                    return "插件已启用。";
                }
                var disabled = new HashSet<string> { instance.Id };
                bool added;
                do
                {
                    added = false;
                    var remaining = current.Instances.Where(p => p.Enabled && !disabled.Contains(p.Id)).ToArray();
                    var unavailable = current.Instances.Where(p => disabled.Contains(p.Id)).Select(p => p.Plugin)
                        .Except(remaining.Select(p => p.Plugin)).ToHashSet();
                    foreach (var other in remaining)
                        if ((catalog.Find(other).Manifest.Dependencies ?? []).Any(unavailable.Contains)) added |= disabled.Add(other.Id);
                } while (added);
                var items = current.Instances.Select(p => disabled.Contains(p.Id) ? p with { Enabled = false } : p);
                if (action == "remove-instance") items = items.Where(p => p.Id != instance.Id);
                await config.Save(current with { Instances = items.ToArray() });
                return "已停用：" + string.Join("、", current.Instances.Where(p => disabled.Contains(p.Id)).Select(p => Name(p.Plugin)));
            }) { ManagedPlugins = () => Inventory(catalog, config.Current, coreSnapshot, runtime),
                WatchManagedPlugins = changed => { config.Changed+=changed; return new Cleanup(()=>config.Changed-=changed); } });

    }

    private static string Name(string id) => id switch
    {
        "accounts" => "邮箱账户与收发", "local-mail" => "本地邮件与归档", "mail-selectors" => "常用账户和文件夹",
        "reminders" => "提醒与通知", "qq-mail" => "QQ 邮箱", "netease-mail" => "网易 163 邮箱", "updates" => "软件更新",
        "core.platform" => "基本运行组件", "welcome" => "使用帮助", "prototype" => "开发示例", "selector" => "示例账户选择",
        _ => id
    };

    private static IReadOnlyList<ManagedPlugin> Inventory(PluginCatalog catalog, PluginConfiguration ordinary, PluginConfiguration core, PluginRuntime runtime)
    {
        var configured = ordinary.Instances.Concat(core.Instances).ToArray();
        var result = new List<ManagedPlugin>();
        foreach (var definition in catalog.All)
        {
            var manifest = definition.Manifest;
            var matches = configured.Where(p => p.Plugin == manifest.Id).ToArray();
            var newId = manifest.Id;
            for (int suffix = 2; configured.Any(p => p.Id == newId); suffix++) newId = manifest.Id + "-" + suffix;
            foreach (var item in matches.DefaultIfEmpty())
            {
                var label = manifest.DisplayName ?? Name(manifest.Id);
                if (matches.Length > 1) label += " · " + item!.Id;
                result.Add(new(item?.Id ?? newId, manifest.Id, label, manifest.Version, item?.Version,
                    item?.Enabled == true && item.Version == manifest.Version, item is not null, manifest.Kind == PluginKind.Core,
                    definition.Origin == PluginOrigin.Builtin, manifest.Permissions ?? [], item?.Grants ?? [], manifest.Dependencies ?? []) { NeedsRestart = runtime.NativeNeedsRestart(item?.Id ?? newId) });
            }
        }
        return result.OrderBy(p => p.Core).ThenBy(p => p.Name).ThenByDescending(p => Version.Parse(p.Version)).ToArray();
    }

    private static string Describe(PluginManifest m) => $"{m.DisplayName ?? Name(m.Id)}  {m.Version} · {m.Source}\n"
        + "需要的插件："+((m.Dependencies?.Length??0)==0?"无":string.Join("、",m.Dependencies!.Select(Name)))+"\n"
        + "申请的权限："+((m.Permissions?.Length??0)==0?"无":string.Join("、",m.Permissions!.Select(PluginPermissionName.Display)));
    private static async Task Observe(Task<string> operation, Contributions pages)
    {
        try { pages.Notice = await operation; }
        catch (Exception error) { pages.Notice = "模拟任务未完成：" + error.Message; }
    }
}
