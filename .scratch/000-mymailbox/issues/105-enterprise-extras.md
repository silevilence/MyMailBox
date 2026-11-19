# 105 验证共享邮箱委托与企业受保护邮件范围

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: research
Scope: 可选扩展决策，确认后继续拆分
Estimate: 2–3 人日
Requirements: OPT-02
Blocked by: 35, 33

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

针对实际公司部署确认共享邮箱/代理发送、公共目录及 IRM 的必要能力与限制。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 根据真实权限验证可访问资源，不能因管理员限制而绕过认证或保护。
- [ ] 分开列出共享读取、Send As/On Behalf 和受保护内容，不泛称完整 Exchange 支持。
- [ ] 根据需要生成后续实现任务；未获授权或依赖不支持时提供明确限制。

## Blocked by

- [35 验证公司 Exchange 的真实接入路径](35-exchange-discovery.md)
- [33 通过 Graph 发送并处理邮件状态](33-graph-send.md)

## 外部条件与范围

需公司 IT 授权、代表性账户及企业策略信息。

本项尚未选入首版，不能自行扩大必做范围。决策型任务完成后应生成细化实现任务，再实施所选能力。

## 验证与交付记录

2026-11-19 本地评估已完成。已区分共享读取、委托两种发送身份及受保护内容，列出每一项需要的租户授权。 此可选范围尚未选入首版。证据、限制与后续验收见[可选项本地评估](../../../docs/validation/2026-11-19-task-000-optional.md)。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
