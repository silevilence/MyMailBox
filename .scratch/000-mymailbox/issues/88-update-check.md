# 88 在设置页和后台检查 GitHub 新版本

Spec: 000-mymailbox
Status: needs-info
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-03, REQ-19
Blocked by: 87, 06

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户手动或按设置后台检查更新，查看版本、变更说明及跳过策略，更新源可替换。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 正确处理 V/v 前缀、语义版本、draft/prerelease、稳定通道和 win-x64 资产，不做字符串大小比较。
- [x] 缺少资产、超时、代理、限流与无更新区分，缓存避免高频请求。
- [x] 真实仓库地址来自配置，不嵌入个人 token；以第二测试源证明更新检查是插件能力。

## Blocked by

- [87 验证 NuGet 更新组件与 NSIS 的最小升级](87-updater-spike.md)
- [06 通过普通插件提供并替换页面和选择器](06-ui-contributions.md)

## 外部条件与范围

需在本任务前确认 GitHub 仓库地址；私有仓库授权若需要另行配置。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：内置普通更新插件提供宿主设置页、公开 GitHub Release 检查、后台开关/间隔/通道/跳过版本、持久化缓存和 ETag。仓库来自分发配置 update-source.json，默认值由本仓库 remote 确认，不含个人 token。契约 IUpdateSource / IUpdateService 可替换，插件停止后移除页面、停止后台循环并拒绝旧调用。

`dotnet run --project tests/MyMailBox.Checks --no-restore -- update-checks update-live` 通过：SemVer 数字排序/元数据、draft/prerelease、缺少 x64/签名资产、304、429 Retry-After、407 代理、超时/坏 JSON、重启缓存、跳过恢复及第二测试源通过插件调用。真实公开仓库当前无 Release，实读返回 Current；未下载任何资产。窗口操作仍留到本轮末集中验收，说明见 docs/updater.md。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
