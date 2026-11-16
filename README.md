# MyMailBox

Windows x64 邮件工作空间，Avalonia / .NET 10。当前实现范围见 `.scratch/000-mymailbox/`，此阶段不连接真实邮箱。

## 构建与运行

安装 x64 .NET 10 SDK 后：

```powershell
dotnet build MyMailBox.slnx
dotnet run --project src/MyMailBox.Desktop
dotnet run --project tests/MyMailBox.Checks
./scripts/publish.ps1
```

发布目录为 `artifacts/publish`。双击 `MyMailBox.Desktop.exe`，需要系统安装 x64 .NET 10 Runtime。产物明确为框架依赖发布，无 AOT、裁剪或捆绑运行时。`--runtime-check` 仅验证运行时启动，不打开窗口。

SDK 固定在 global.json，包版本和依赖闭包记录在各项目及 packages.lock.json。此轮构建复用本机 NuGet 缓存中的 Avalonia 12.1.1 和 CordiSharp 0.2.0。

## Profile、插件与演示

默认工作目录为 `%LOCALAPPDATA%/MyMailBox`，可用进程环境变量 `MYMAILBOX_HOME` 指向隔离目录。

- `profiles/default/core.json`：核心实例，只在启动时读取。错误时进入安全修复页，恢复默认值前自动备份。
- `profiles/default/plugins.json`：普通实例，文件编辑或设置页保存后热重载。无效变更保持上次有效运行状态；不要放入凭据或邮件正文。
- `plugins/<id>/<version>/`：插件版本文件。插件管理页先预览 ZIP，再安装和启用；安装不会自动授权。
- `logs/`、`exports/`：轮转诊断及用户导出。诊断只保留操作元数据。

首次启动包含欢迎、阅读/写信/自动化/恢复原型、账户选择器、插件设置、插件管理和权限诊断。所有邮件场景均为明确标注的模拟原型，没有真实邮箱收发能力。

构建解决方案后执行 `./scripts/package-samples.ps1`，可从 `artifacts/packages` 安装 example v1/v2、alternative-selector 和 native 样例。替换选择器前停用默认 selector 实例；外部实现无需修改宿主。native 需要显式授予 `ui.native`，它用于验证高级控件的边界：此版本 Avalonia 的自定义动画控件不能可靠热卸载，需重启。普通页面/选择器支持经过验证的热卸载。

实例配置示例：

```json
{
  "schemaVersion": 1,
  "instances": [
    { "id": "example", "plugin": "example", "version": "1.0.0", "enabled": true,
      "options": { "greeting": "你好" }, "grants": [] }
  ]
}
```

权限以 capability 或 `capability:accountId` 表示，例如 `mail.read:work`；插件同时声明且用户授予后，才能调用相应宿主能力。当前基础层暴露入口，实际邮件能力由后续工单提供。同进程插件必须可信，这不是隔离沙箱。

## 验证

`./scripts/verify.ps1` 执行完整非 UI 验证；本轮所有工作完成后执行 `./scripts/verify.ps1 -Ui` 集中运行 FlaUI。UI 工具还需要本机 Windows Desktop Runtime；正式 Avalonia 应用不依赖它。

验收范围、修复和高级控件限制见 [验收记录](docs/validation/framework-plugins.md)，交互规范见 [ux-flows.md](docs/ux-flows.md)，插件依赖及上游边界见 [cordisharp.md](docs/cordisharp.md)。
