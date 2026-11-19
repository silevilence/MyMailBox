# 84 联调各提供者的故障与恢复矩阵

Spec: 000-mymailbox
Status: needs-info
Progress: in-progress
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-05, REQ-21, SUP-01, SUP-02
Blocked by: 47, 78, 46, 35, 29, 30, 34, 33

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

在已选提供者上系统验证断网、凭据失效、限流、服务器变动、磁盘满和插件崩溃。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] IMAP/POP3/Graph 与实际公司 Exchange 都有适用测试结果，不能以模拟或个人邮箱替代公司验收。
- [x] 错误分类和恢复入口一致，故障不阻塞其他账户及用户编辑。
- [x] 关键副作用操作可对账，日志脱敏；已接入提供者的认证/权限变化能恢复。

## Blocked by

- [47 使用外部邮箱插件验证统一提供者契约](47-provider-sdk.md)
- [78 审查自动化历史并从失败步骤继续](78-task-history.md)
- [46 处理多客户端修改删除和目录冲突](46-sync-conflicts.md)
- [35 验证公司 Exchange 的真实接入路径](35-exchange-discovery.md)
- [29 通过 QQ 预设完成收发闭环](29-qq.md)
- [30 通过 163 预设完成收发闭环](30-netease.md)
- [34 增量同步 Graph 邮箱并恢复失效游标](34-graph-sync.md)
- [33 通过 Graph 发送并处理邮件状态](33-graph-send.md)

## 外部条件与范围

需复用已授权的各真实测试账户，公司路线的实现和实测是完成条件。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

条件门槛：必须先完成公司接入验证，再按其结论完成本地 Exchange 的读/写/同步任务，或组织 Graph 路线；若只能降级为通用协议，须记录用户接受的功能边界。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：统一收件箱已接入 Outlook.com 的已授权收件箱、分页游标、同日期翻页、原文离线阅读和回复转发。provider-faults 真实运行接收队列，验证离线、重新授权、Retry-After 和 SQLite FULL 时邮件与游标回滚；multi-sync、mailbox-provider 验证多账户隔离、停用与缓存。protocol-live、protocol-faults、imap-sync、imap-conflicts 本次使用现有 GreenMail 和本机故障服务器通过，exchange-eas 验证协议替身。邮箱插件和 ActiveSync 的已读增量保留离线原文。外部插件的错误会保留故障类别和重试等待，诊断日志被独占不会中断原操作。仍缺 QQ/163/个人 Graph 和公司 Exchange 的授权服务端记录。

- 本轮本地验证：认证、证书信任、目录权限、429 等待、断线、数据盘满及已发送无回执已用提供者样本验证。日志脱敏有独立诊断检查。生产者端权限变化和跨厂商表现保留真实验收。
