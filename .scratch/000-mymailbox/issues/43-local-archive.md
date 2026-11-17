# 43 创建本地邮箱并按目录保存归档

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-05, REQ-18
Blocked by: 18, 12

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户新建、打开或关闭本地邮箱，创建目录并接收完整原文归档。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 本地邮箱有独立 ID、名称/标签等元数据，关闭/移除账户不删除底层归档。
- [x] 归档包含完整 MIME 与附件，重启后可脱网阅读。
- [x] 目录式格式版本、校验与备份入口清楚，单文件运行格式列为另选范围。

## Blocked by

- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)
- [12 在写信与整理入口选择账户目录和身份](12-selectors.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：本地邮箱管理页面支持新建/关闭/重新打开保留邮箱、多级目录和完整 EML 归档。独立 UUID、名称、alias/标签保留，关闭或移除不删除底层数据；阅读/导出过滤不可用邮箱。目录式工作空间 v1、完整性检查、备份与归档搬运入口已说明。dotnet build 与 local-mailboxes 定向检查通过：重启、附件字节保真、关闭拒绝访问、移除后重开、alias 冲突重映射、目录边界、完整性和备份。真实页面流程在本轮末集中验收。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
