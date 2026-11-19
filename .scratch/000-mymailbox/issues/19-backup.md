# 19 备份并恢复完整工作空间

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: SUP-01, REQ-09
Blocked by: 18, 11

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户创建一致性备份并恢复到新位置，保留账户引用、目录和原文，凭据重新授权。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 活跃写入时生成一致性备份，覆盖数据库日志/对象文件，校验损坏可报告。
- [x] 恢复保留稳定 ID 和引用，冲突策略清楚，索引可重建。
- [x] 迁移失败可恢复到备份；不把直接复制 SQLite 主文件当完整备份。

## Blocked by

- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)
- [11 管理账户身份及仅收仅发能力](11-accounts.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：通过 IWorkspaceBackup 与内置备份页面生成版本化 ZIP。SQLite BackupDatabase 在线快照覆盖 WAL 已提交内容；配置/插件/插件数据以一致性快照检查复制，逐文件 SHA-256，账户凭据及可丢弃缓存从备份副本移除并 VACUUM。仅恢复到不存在的新目录，经清单、路径、哈希与数据库完整性检查后切换；失败保留隔离诊断目录与原备份。界面可在新窗口打开成功恢复的工作空间。`dotnet run --project tests/MyMailBox.Checks --no-restore -- backup` 通过活跃写入、恢复身份/配置/原文、凭据排除、损坏、冲突、取消与重建检查。窗口验收集中执行。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：并发收信时复现 SQLite 的 BUSY_RECOVERY，在线快照在短暂忙锁后重试。备份检查连续八次验证锁释放后成功、持锁时取消、原始数据与授权剔除。
