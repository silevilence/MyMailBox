# 49 预览并导入 Outlook 在线账户配置

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-06
Blocked by: 48, 11, 12

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

导入向导列出可发现账户和连接字段，用户选择后生成本应用账户并完成重新授权。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 逐字段展示已导入、需补充、不支持及来源信息，支持显示名/标签与重复账户映射。
- [ ] 取消不创建半成品账户；密码/token 不作为跨客户端无损搬运承诺。
- [ ] 导入的账户可接收或发送并保存稳定引用；仅显示邮箱地址的演示不算验收完成。

## Blocked by

- [48 验证本机 Outlook 配置读取及位数边界](48-outlook-discovery.md)
- [11 管理账户身份及仅收仅发能力](11-accounts.md)
- [12 在写信与整理入口选择账户目录和身份](12-selectors.md)

## 外部条件与范围

需支持版本 Outlook 和授权测试账户。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
