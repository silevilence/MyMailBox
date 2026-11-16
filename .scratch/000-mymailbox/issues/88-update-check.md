# 88 在设置页和后台检查 GitHub 新版本

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
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

- [ ] 正确处理 V/v 前缀、语义版本、draft/prerelease、稳定通道和 win-x64 资产，不做字符串大小比较。
- [ ] 缺少资产、超时、代理、限流与无更新区分，缓存避免高频请求。
- [ ] 真实仓库地址来自配置，不嵌入个人 token；以第二测试源证明更新检查是插件能力。

## Blocked by

- [87 验证 NuGet 更新组件与 NSIS 的最小升级](87-updater-spike.md)
- [06 通过普通插件提供并替换页面和选择器](06-ui-contributions.md)

## 外部条件与范围

需在本任务前确认 GitHub 仓库地址；私有仓库授权若需要另行配置。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
