# 发布校验

正式发布遵循 AGENTS.md：指定版本时先使用 generate-changelog 同步变更记录与全部耦合版本，再提交、打 V 前缀 Tag、推送，由 Actions 构建发布。单独运行检查不构成发布授权。

- `pwsh -NoProfile -File scripts/check-release.ps1 -Tag V0.1.0 -Phase Prepare`：检查准备内容，拒绝已存在的规范化版本。当前尚无真实 changelog，此命令应报告缺失，不会自行生成。
- `-Phase Tagged`：用于 CI，要求实际触发 Tag（可为历史小写 v）存在、HEAD 是该精确提交且无未提交变更。
- `-OutputDirectory <新目录>`：输出 release-body.md 与 release-metadata.json，供打包/Release 使用。
- `pwsh -NoProfile -File scripts/test-release.ps1`：临时仓库夹具；不修改本仓库 Tag。

耦合版本范围为根 Directory.Build.props 与 src 下应用/契约/运行时/内置能力项目的有效 MSBuild Version。samples、tests 和 PackageReference 第三方版本不作为本应用版本。内置插件 manifest 的 1.0.0 是插件协议版本，独立于应用分发版本。
