# 46 处理多客户端修改删除和目录冲突

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
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

- [x] 远端删除、本地待改、目录重命名和游标重建有确定冲突策略及可见结果。
- [x] 原始消息重复/Message-ID 缺失不会覆盖不同邮件。
- [x] 离线缓存与独立归档语义不同，远端删除不能误清理已归档唯一副本。

## Blocked by

- [21 增量接收 IMAP 邮件并离线阅读](21-imap-sync.md)
- [41 管理目录及邮件标签已读星标](41-folders-flags.md)
- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：IMAP 同步在所选范围内按 UID 核对标记和已移除邮件，并覆盖范围外有待处理修改的 UID。可在离线同步页面排队单项已读或星标，下次仅写该位并回读确认；其他客户端改动的独立标记保留。目录失效或来源消失时保留冲突记录，不按主题或 Message-ID 猜测新来源。服务器移除的条目退出统一收件箱，已下载 MIME 与跨邮箱独立副本仍可阅读。

`imap-conflicts`、`imap-sync` 在复用的本机 GreenMail 通过。第二个客户端执行设已读、定向删除和目录改名，验证离线改星、冲突提示、原文保留、旧引用不绑定新目录、重复或缺失 Message-ID 不覆盖其他邮件。UIDVALIDITY 变化另由持久化旧游标注入验证。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
