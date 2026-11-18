# 75 按计划把超过 30 天的邮件归档到本地

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-12, REQ-13, REQ-14, REQ-15
Blocked by: 74, 45, 72

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

完成用户核心示例：每天检查指定邮箱，将符合条件的旧邮件移动到本地年/月目录。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 配置、命中预览、执行、通知和历史构成完整流程，可先试运行不写入。
- [x] 日期边界以固定本次时刻计算，目标持久化后才清理源，星标/保留标签可排除。
- [x] 暂停、重启、磁盘满和任务重叠均能恢复，不重复归档、不丢唯一副本。

## Blocked by

- [74 独立选择任务实际处理的邮件集合](74-task-targets.md)
- [45 恢复跨邮箱操作的每个中断点](45-move-recovery.md)
- [72 恢复错过的调度并控制重叠与重试](72-scheduler-recovery.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：task-archive 与 task-archive-live 通过。实际 GreenMail 旧信按固定日期移入本地 2025/04，目标写入故障保留远端，恢复核验后才清理来源。定向检查覆盖只读预演、暂停、重启、重叠、星标变化、每日触发、通知和历史。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
