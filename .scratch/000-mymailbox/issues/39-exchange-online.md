# 39 通过 Microsoft 365 组织账户完成收发

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: task
Scope: 条件路线：需要 Exchange Online 时
Estimate: 2–3 人日
Requirements: REQ-05, REQ-20, REQ-21
Blocked by: 32, 33, 34

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

在 Graph 提供者上补充组织租户、授权策略及 Microsoft 365 账户体验。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 组织账户完成收发和增量同步，不把个人账号成功当组织账号验收。
- [ ] 租户拒绝、管理员同意或条件访问阻止时提示可行处理，不尝试绕过策略。
- [ ] 公司发现任务选择云路线时，本任务结果覆盖实际公司邮箱，而不是另测无关租户。

## Blocked by

- [32 登录 Outlook.com 并阅读云端邮件](32-graph-login.md)
- [33 通过 Graph 发送并处理邮件状态](33-graph-send.md)
- [34 增量同步 Graph 邮箱并恢复失效游标](34-graph-sync.md)

## 外部条件与范围

需要授权测试租户；若公司非云部署，作为扩展接入范围验收。

本项按父规格的条件路线或评估结论决定是否实施；未选中时记录理由，不伪装为已实现。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
