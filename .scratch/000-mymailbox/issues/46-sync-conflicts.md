# 46 处理多客户端修改删除和目录冲突

Spec: 000-mymailbox
Status: ready-for-agent
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-21, SUP-01
Blocked by: 21, 41, 18

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

在另一客户端修改状态、移动或删除时，本应用同步与离线操作进行一致性处理。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 远端删除、本地待改、目录重命名和游标重建有确定冲突策略及可见结果。
- [ ] 原始消息重复/Message-ID 缺失不会覆盖不同邮件。
- [ ] 离线缓存与独立归档语义不同，远端删除不能误清理已归档唯一副本。

## Blocked by

- [21 增量接收 IMAP 邮件并离线阅读](21-imap-sync.md)
- [41 管理目录及邮件标签已读星标](41-folders-flags.md)
- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
