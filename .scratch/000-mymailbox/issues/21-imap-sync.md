# 21 增量接收 IMAP 邮件并离线阅读

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
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

- [x] 支持同步范围和轮询/IDLE 适配，不会反复全量下载已有内容。
- [x] UIDVALIDITY 改变、断线重连和游标失效可恢复，映射含账户/目录/UIDVALIDITY/UID。
- [x] 正文未缓存与不存在分开表示；批量同步中可打开邮件和取消任务。

## Blocked by

- [20 添加 IMAP 账户并浏览真实目录摘要](20-imap-connect.md)
- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)
- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：邮箱连接与收发 → 离线同步，选择目录和最近邮件数量。立即同步按 32 封一批保存最新摘要，持续接收优先使用 IDLE，无 IDLE 时每分钟轮询。打开仅有摘要的邮件时下载包含附件的 MIME，之后可在本页离线阅读，或在本地邮箱对应目录打开完整阅读器。停止同步保留已提交批次。

定向命令 `dotnet run --project tests/MyMailBox.Checks --no-restore -- imap-sync` 通过。复用本机 GreenMail、TLS 证书、Java、NuGet 缓存。覆盖最近范围与扩大范围、首批取消再启动、服务器新增邮件、登录失败后重连、重启时无有效密码仍能读取缓存、已移除与未缓存区分、附件原文保留。通过注入旧 UIDVALIDITY 复验目录重建与旧引用拒绝。集中窗口验收另记。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
