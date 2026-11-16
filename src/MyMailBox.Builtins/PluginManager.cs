using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public static class PluginManager
{
    public static IDisposable Register(Contributions pages, PluginPackages packages, PluginCatalog catalog, HotConfiguration config, string corePath)
        => pages.Add(new Page("plugin-manager", "插件管理", "先预览插件的来源、版本、依赖和权限，再安装。仅运行你信任的同进程插件；包校验和用于检测损坏，不证明发布者身份。",
            [new("package-path", "插件包（ZIP）", FilePicker: true), new("plugin-id", "插件 ID", "example"), new("plugin-version", "版本", "1.0.0"),
             new("managed-instance", "实例 ID", "example"), new("grants", "授予权限（逗号分隔；预览后填写）")],
            [new("inspect-package", "预览插件包"), new("install-package", "安装插件包"), new("enable-plugin", "启用实例"), new("disable-plugin", "停用实例"),
             new("remove-instance", "移除实例配置"), new("remove-package", "移除版本文件"), new("list-plugins", "查看插件状态")], async (values, action) =>
            {
                if (action == "inspect-package")
                {
                    var manifest = packages.Inspect(values["package-path"]);
                    return Describe(manifest) + "\n完整性与 SDK 检查通过。安装只保存文件，不自动启用或授予权限。";
                }
                if (action == "install-package") return Describe(packages.Install(values["package-path"]).Manifest) + "\n已安装到独立版本目录，可继续启用。";
                if (action == "list-plugins") return string.Join("\n", catalog.All.Select(p => Describe(p.Manifest))) + "\n\n实例：\n" + string.Join("\n", config.Current.Instances.Select(p => $"{p.Id} → {p.Plugin}@{p.Version} · {(p.Enabled ? "启用" : "停用/等待依赖")}"));
                var instance = new PluginInstance(values["managed-instance"], values["plugin-id"], values["plugin-version"], Grants: values["grants"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                var definition = catalog.Find(instance);
                if (action == "remove-package") { packages.Remove(definition, config.Current, ConfigurationFiles.Read(corePath)); return "指定版本已移除，其他版本与用户数据保留。"; }
                if (definition.Manifest.Kind == PluginKind.Core) return "核心插件请编辑 core.json；更新在下次启动生效。";
                var current = config.Current;
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
}
