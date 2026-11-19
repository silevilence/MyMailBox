using MyMailBox.Contracts;
using MyMailBox.Runtime;

namespace MyMailBox.Builtins;

public static class DiagnosticsPage
{
    public static IDisposable Register(Contributions pages, PluginRuntime runtime, HotConfiguration config, PluginCatalog catalog, Diagnostics diagnostics, string workspace)
        => pages.Add(new Page("diagnostics", "权限与诊断", "权限限制宿主入口。同进程可信插件仍可直接调用 .NET API，本机制不是恶意代码安全沙箱。导出只含实例、操作、结果、错误代码和关联 ID，不包含异常正文、配置、密码、令牌或邮件正文。",
            [], [new("view-permissions", "查看权限与运行状态"), new("export-diagnostics", "导出脱敏诊断")], (_, action) =>
            {
                if (action == "export-diagnostics") return Task.FromResult("诊断已导出：" + diagnostics.Export(Path.Combine(workspace, "exports", "diagnostics-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json")));
                var lines = config.Current.Instances.Select(p => $"{p.Id} → {p.Plugin}@{p.Version}\n声明：{string.Join(", ", catalog.Find(p).Manifest.Permissions ?? [])}\n授予：{string.Join(", ", p.Grants ?? [])}");
                var logStatus = diagnostics.MissedRecords > 0 ? $"本次运行有 {diagnostics.MissedRecords} 条诊断未能写入，请检查日志目录空间或占用。\n\n" : "";
                return Task.FromResult(logStatus + string.Join("\n\n", lines) + "\n\n" + string.Join("\n", runtime.States.Select(p => p.Key + "：" + p.Value)) + (config.Error is null ? "" : "\n配置错误：" + config.Error));
            }));
}
