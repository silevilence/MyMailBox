# 93 验证从已安装旧版到 Actions 新版的升级

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-02, REQ-03, REQ-24, SUP-01
Blocked by: 92, 89

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

以指定测试版本演示系统 .NET 环境下安装、检查、下载、更新、重启及失败恢复。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 带有账户/草稿/归档/任务数据的旧版升级后内容和引用正确，不误删或重复运行自动化。
- [ ] 覆盖缺失运行时、损坏更新、升级中断和版本不兼容；安装器与应用显示一致状态。
- [ ] 在线下载功能验证一次，其余使用已有产物；发布后通过平台摘要/大小/CI 记录核对，不全量回下载验哈希。

## Blocked by

- [92 由 V 或 v Tag 自动发布完整 GitHub Release](92-tag-release.md)
- [89 下载验证并安装更新且保留用户数据](89-update-install.md)

## 外部条件与范围

需要授权的测试 Release；本任务不自动赋予发布生产版本权限。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
