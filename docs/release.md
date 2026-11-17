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

最终集中验收依次覆盖：无运行时、仅 x86、有效 x64；中文自定义路径安装与启动、覆盖升级、默认保留数据卸载、显式删除数据卸载。复用本机现有运行时和 WebView2 资源准备隔离环境，不重新下载。
