# 18 在磁盘满和崩溃后恢复本地邮箱

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: SUP-01, REQ-05
Blocked by: 10, 13

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

将本地工作空间写入、索引和缓存管理串成可恢复操作，用户能检查完整性与释放可丢弃缓存。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 在保存原文/元数据/索引各阶段中断后重启，已完成邮件完整、未完成操作可恢复。
- [x] 区分服务器缓存与唯一归档，清缓存不能删除唯一副本；磁盘满时保留来源。
- [x] 数据版本可迁移，索引可重建，孤立对象可诊断而非静默删除。

## Blocked by

- [10 打开一封本地邮件并持久保存到工作空间](10-local-inbox.md)
- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：原文/元数据/SQLite 索引在同一事务提交，新增版本 0→1 原子迁移、未来版本拒绝、SHA-256、完整性与孤立引用诊断、原文驱动的摘要/索引重建。独立 disposable_cache 表清理不会触及唯一原文；当前本地邮件均为唯一副本。`dotnet run --project tests/MyMailBox.Checks --no-restore -- storage mail` 通过三个阶段子进程强制终止后重开、实际 SQLITE_FULL（限制数据库页数）、缓存隔离、损坏诊断、迁移和重建检查。未提交事务自动回滚，源 EML 始终保留，可重新导入。窗口验收集中执行。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
