# 33 通过 Graph 发送并处理邮件状态

Status: needs-info
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-05, REQ-20
Blocked by: 32, 26

## Parent

[MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

在已有写信和阅读流程中使用 Graph 发送、保存草稿、修改已读与移动邮件。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 个人账户发送、回复及附件路径按实际 API 能力验证，超限有明确结果。
- [ ] 移动后维护稳定本地 ID 与远端映射，避免消息失联。
- [ ] 跨邮箱导入能力未支持时明确禁用，不能用重新发送代替移动。

## Blocked by

- [32 登录 Outlook.com 并阅读云端邮件](32-graph-login.md)
- [26 回复全部和转发时保留上下文与身份](26-reply-forward.md)

## 外部条件与范围

沿用 Graph 授权测试环境。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
