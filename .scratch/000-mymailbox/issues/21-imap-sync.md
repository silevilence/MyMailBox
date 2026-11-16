# 21 增量接收 IMAP 邮件并离线阅读

Spec: 000-mymailbox
Status: ready-for-agent
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-21, REQ-22, REQ-23
Blocked by: 20, 13, 18

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

首次同步先展示最近摘要，正文/附件按需加载；之后增量同步并在离线时保留可读内容。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 支持同步范围和轮询/IDLE 适配，不会反复全量下载已有内容。
- [ ] UIDVALIDITY 改变、断线重连和游标失效可恢复，映射含账户/目录/UIDVALIDITY/UID。
- [ ] 正文未缓存与不存在分开表示；批量同步中可打开邮件和取消任务。

## Blocked by

- [20 添加 IMAP 账户并浏览真实目录摘要](20-imap-connect.md)
- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)
- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
