# 发布校验

正式发布遵循 AGENTS.md：指定版本时先使用 generate-changelog 同步变更记录与全部耦合版本，再提交、打 V 前缀 Tag、推送，由 Actions 构建发布。单独运行检查不构成发布授权。

- `pwsh -NoProfile -File scripts/check-release.ps1 -Tag V0.1.0 -Phase Prepare`：检查准备内容，拒绝已存在的规范化版本。当前尚无真实 changelog，此命令应报告缺失，不会自行生成。
- `-Phase Tagged`：用于 CI，要求实际触发 Tag（可为历史小写 v）存在、HEAD 是该精确提交且无未提交变更。
- `-OutputDirectory <新目录>`：输出 release-body.md 与 release-metadata.json，供打包/Release 使用。
- `pwsh -NoProfile -File scripts/test-release.ps1`：临时仓库夹具；不修改本仓库 Tag。

耦合版本范围为根 Directory.Build.props 与 src 下应用/契约/运行时/内置能力项目的有效 MSBuild Version。samples、tests 和 PackageReference 第三方版本不作为本应用版本。内置插件 manifest 的 1.0.0 是插件协议版本，独立于应用分发版本。

## NSIS 安装包

先运行 `scripts/publish.ps1`，再运行 `scripts/package-installer.ps1`。脚本复用已安装的 NSIS，生成 win-x64 安装程序、SHA256SUMS.txt、build-manifest.json；不下载或捆绑 .NET / WebView2。安装器实际调用 x64 dotnet 的 `--list-runtimes` 确认 Microsoft.NETCore.App 10，缺失退出码 1638，架构错误退出码 1633。SDK 只有在同时带有要求的运行时时才满足条件。

默认程序目录为 `%LOCALAPPDATA%\Programs\MyMailBox`，工作空间为 `%LOCALAPPDATA%\MyMailBox`。允许自定义程序路径，升级到相同路径前应退出应用。安装器写入当前用户快捷方式和卸载信息。卸载逐项删除安装包文件，保留不属于安装包的文件；默认保留工作空间。交互卸载可明确勾选并再次确认删除默认工作空间；静默卸载始终保留数据，自定义工作空间及导出备份不在删除范围内。

2026-11-17 集中验收已覆盖：无运行时、仅 x86、有效 x64；中文自定义路径安装与启动、覆盖升级、默认保留数据卸载、显式删除数据卸载。复用本机现有运行时和 WebView2 资源准备隔离环境，不重新下载；具体版本、日志与最终安装摘要见 [整体验收](validation/2026-11-17-independent-tasks.md)。

## 持续构建

`.github/workflows/build.yml` 仅通过手动 `workflow_dispatch` 或发布流程的 `workflow_call` 运行 Windows 2025 构建；普通分支 push 和 PR 不自动触发。checkout 指向事件的精确 SHA，权限仅 contents:read。`scripts/ci-build.ps1 -ExpectedCommit <40 位 SHA>` 拒绝脏的受跟踪文件或错误 SDK，执行完整非 UI 检查、发布启动/缺失运行时检查、版本校验夹具与 NSIS 编译。所有检查成功后才上传 14 天保留的构建 artifact，不创建 Tag 或 Release。

工作流变更须通过 Actions 语义校验（例如 `actionlint`），普通 YAML 解析不能验证表达式上下文。测试工作空间在实际构建 step 的 `env` 中引用 `runner.temp`，不能把 `runner` 放到 job 级 `env`；否则 GitHub 在调度任务前即报无效工作流。

SDK 10.0.301 在 global.json 中固定并禁止 roll-forward；NuGet 使用锁文件与 setup-dotnet 缓存，NSIS 3.12 先检查本机/缓存，缺失才安装固定的 3.12.0 Chocolatey 包。固定版本依据：[NSIS 3.12 官方发布](https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/)。Actions 使用完整 commit SHA，升级时核对官方 tag：[checkout](https://github.com/actions/checkout/releases/tag/v7.0.1)、[setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0)、[cache](https://github.com/actions/cache/releases/tag/v4.3.0)、[upload-artifact](https://github.com/actions/upload-artifact/releases/tag/v7.0.1)。

产物包含安装器、SHA256SUMS.txt、build-manifest.json、ci-manifest.json（commit/SDK/RID/发布方式）及 publish-files.json（每个发布文件的相对路径/大小/SHA256）。本地运行与真实 GitHub Actions 的验收证据分开记录；仅提交 workflow 并不等于 CI 已通过。

## Tag Release 工作流

Graph 登录应用标识由仓库 Actions Secret `MYMAILBOX_GRAPH_CLIENT_ID` 提供，通过环境变量注入构建生成的程序集元数据。真实值不进入受跟踪源码；Tag 构建缺少或配置无效时失败。复用 build 工作流显式接收此 Secret，PR 构建不注入。普通无 Secret 构建仍可运行其他功能及替身测试，但无法进行真实微软登录。本地开发使用被忽略的根目录 `Directory.Build.local.props` 中的 `MyMailBoxGraphClientId` 属性。

`.github/workflows/release.yml` 同时监听 V* 与 v*。构建 job 只读权限，调用相同 Windows 构建并校验合法 SemVer、精确 Tag 提交、所有耦合版本与已提交 changelog。仅 publish job 有 contents:write；从当前 run 下载构建 artifact，重新核对 changelog 和 Tag，再签名、上传草稿、校验及公开。不使用自动生成的提交列表作为正文。

准备正式发布前，把 `src/MyMailBox.Builtins/update-source.json` 的 publicKey 设置为真实 Ed25519 公钥，并在 GitHub Actions Secrets 配置 `MYMAILBOX_ED25519_SEED`（base64 编码的 32 字节 seed）。ReleaseTools 使用已经固定的 NetSparkle 配套 Ed25519 库签名，拒绝缺失 Secret 或公私钥不匹配；私钥不写到磁盘、不写入客户端、不输出日志。当前仓库公钥为空，流程将明确失败，不会自动生成正式密钥。

发布资产为 NSIS 安装器、SHA256SUMS.txt、appcast.xml、appcast.xml.signature、build-manifest.json、ci-manifest.json、publish-files.json。签名 appcast 的 enclosure 包含安装器签名与插件 SDK 版本。实际下载 URL 保留触发 Tag 的大小写，展示版本统一 V。所有资源的 GitHub state/size/digest 都通过后才把 draft 改为 false；预发布设置 prerelease 并禁止成为稳定 latest。

同 Tag 重跑可发现已存在草稿，复用摘要一致的上传，替换不一致的草稿资产。公开 Release 只作只读比对，差异报错而不覆盖；不移动或重建 Tag。上传验证依据 [GitHub Release API](https://docs.github.com/en/rest/releases/releases) 与 [资产摘要 API](https://docs.github.com/en/rest/releases/assets)，不重新下载全部安装资产核对哈希。

`scripts/test-publish-release.ps1` 使用本地模拟 transport 验证发布状态机，`release-signing` 控制台组验证真实签名工具。两者不是 GitHub Actions / 真实 Release 验收。正式授权发布时仍须先运行 generate-changelog 技能并完成提交、Tag 与推送；本轮仅准备流程，未触发发布。
