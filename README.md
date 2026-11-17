# MyMailBox

Windows x64 邮件工作空间，Avalonia / .NET 10。当前支持本地 EML 阅读、归档导出、账户管理、通用 IMAP 目录摘要、SMTP 纯文本发送、POP3 收取、提醒计划和插件扩展。Outlook/PST 导入与提供者专属接入仍在开发，任务进度见 [需求索引](.scratch/000-mymailbox/index.md)。

## 当前功能

- 导入完整 EML，离线阅读纯文本、MIME 结构、原文和安全 HTML；支持编码切换、CID 图片、附件预览与另存。远程多媒体默认阻止，可按单封、发件地址或精确域名放行。
- 创建本地邮箱与目录，关闭后保留归档；管理账户名称、alias、标签、颜色与能力，按稳定 ID 或动态标签保存选择范围。
- 在“邮箱连接与收发”分别配置收信与发信服务器、加密方式、凭据和代理；IMAP 只读浏览目录和分页摘要，SMTP 发送 To/Cc/Bcc 纯文本，POP3 下载到本地、重启去重并默认保留服务器副本。保存配置不会自动联网。当前真实验证使用本机 GreenMail，QQ/163/Outlook.com/公司 Exchange 的专属授权及增量同步另按工单交付。
- “Outlook.com 邮件”已实现个人账户只读 Graph 接入：应用注册由发布者内置维护，用户直接登录自己的微软账户，无需填写 Client ID。支持受保护缓存、目录/摘要分页及 MIME/附件下载；仍待个人账户真实验收，尚未标为完成。见[使用步骤与发布者配置](docs/validation/outlook-com-setup.md)。
- 导出 EML、归档包、CSV/JSON、附件、PDF/HTML/TXT 阅读副本；工作空间一致性备份可恢复到新目录。PDF 与 HTML 阅读需要 WebView2 Runtime。
- 保存一次性提醒和循环计划，支持日期规则、5 字段 CRON、时区、错过补跑、重叠控制、重试与执行记录。仅在应用和提醒插件运行期间执行，完全退出后等待下次启动恢复。
- 通过 GitHub Releases 检查版本；已实现签名下载与 NSIS 更新交接，但正式签名公钥尚未配置，当前不能下载安装正式更新。

## 构建与运行

安装 `global.json` 指定的 x64 .NET SDK 10.0.301 后：

```powershell
dotnet build MyMailBox.slnx
dotnet run --project src/MyMailBox.Desktop
dotnet run --project tests/MyMailBox.Checks
./scripts/publish.ps1
# 已安装 NSIS 3.12 时生成安装包
./scripts/package-installer.ps1
```

发布目录为 `artifacts/publish`。双击 `MyMailBox.Desktop.exe`，需要系统安装 x64 .NET 10 Runtime。产物明确为框架依赖发布，无 AOT、裁剪或捆绑运行时。`--runtime-check` 仅验证运行时启动，不打开窗口。

SDK 固定在 global.json，包版本和依赖闭包记录在各项目及 packages.lock.json。主要组件为 Avalonia 12.1.1、CordiSharp 0.2.0、MailKit/MimeKit 4.17.0、SQLite、Cronos 和 NetSparkle。已有安装包、运行时和 NuGet 缓存应优先复用。

## Profile、插件与演示

默认工作目录为 `%LOCALAPPDATA%/MyMailBox`，可用进程环境变量 `MYMAILBOX_HOME` 指向隔离目录。

- `profiles/default/core.json`：核心实例，只在启动时读取。错误时进入安全修复页，恢复默认值前自动备份。
- `profiles/default/plugins.json`：普通实例，文件编辑或设置页保存后热重载。无效变更保持上次有效运行状态；不要放入凭据或邮件正文。
- `plugins/<id>/<version>/`：插件版本文件。插件管理页先预览 ZIP，再安装和启用；安装不会自动授权。
- `logs/`、`exports/`：轮转诊断及用户导出。诊断只保留操作元数据。
- `mail/mail.db`：原始 MIME、账户、目录、提醒与调度状态。凭据单独使用 Windows 当前用户保护；备份不包含凭据。
- `plugin-data/`、`updates/`：插件设置及更新下载、安装状态、独立恢复备份。

首次启动启用本地邮件、账户、选择、提醒和更新插件，并保留明确标注的阅读/写信/自动化/恢复模拟原型。旧工作空间不会被自动重写插件配置，可在插件管理页启用新增内置插件；普通插件版本仍为 1.0.0。

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

权限以 capability 或 `capability:accountId` 表示，例如 `mail.read:work`；插件同时声明且用户授予后，才能调用相应宿主操作入口。协议提供者与完整自动化动作 SDK 尚待后续工单。同进程插件必须可信，这不是隔离沙箱。

## 验证

`./scripts/verify.ps1` 执行完整非 UI 验证；本轮所有工作完成后执行 `./scripts/verify.ps1 -Ui` 集中运行 FlaUI。UI 工具还需要本机 Windows Desktop Runtime；正式 Avalonia 应用不依赖它。

控制台检查自动使用隔离测试工作空间；可追加组名运行定向检查，例如 `dotnet run --project tests/MyMailBox.Checks -- mime html readable-export`。界面测试使用合成数据，`MYMAILBOX_UI_OUTPUT` 可指定证据目录，`MYMAILBOX_UI_EXE` 可指向已安装的测试应用。

构建与发布契约见 [release.md](docs/release.md)，更新机制和当前限制见 [updater.md](docs/updater.md)。正式发布必须经过版本与 changelog 准备、提交、Tag 和推送；本地验证不会发布 Release。

最新 PST ANSI 修复及选型见 [50 验收](docs/validation/2026-11-17-pst-ansi-fix.md)，公司端点及两次 401 结果见 [35 调查](docs/validation/2026-11-17-exchange-discovery.md)。前轮协议及 Outlook/PST 结果见 [集中验收](docs/validation/2026-11-17-protocol-outlook.md)，完整结果见 [独立任务整体验收](docs/validation/2026-11-17-independent-tasks.md)。早期插件基线见 [01–09 验收记录](docs/validation/framework-plugins.md)，交互规范见 [ux-flows.md](docs/ux-flows.md)，插件依赖及上游边界见 [cordisharp.md](docs/cordisharp.md)。
