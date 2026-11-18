# 42 在同一邮箱内复制移动和删除邮件

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-15, REQ-05
Blocked by: 41

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户从列表选择目标目录执行复制、移动、归档或删除，使用提供者实际能力。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 原生移动与降级复制后删除语义明确，失败不丢唯一副本。
- [x] 垃圾箱删除与永久删除区分，权限/配额错误逐项报告。
- [x] 操作以统一动作接口暴露，后续规则与任务可复用，能撤销时才提供撤销。

## Blocked by

- [41 管理目录及邮件标签已读星标](41-folders-flags.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：在「目录与标记」的移动、复制与删除区选择目标。同账户支持复制、移动、归档、垃圾箱与单独确认的永久删除。本地移动可在邮件仍处于目标且来源目录有效时撤销。IMAP 优先原生 MOVE；降级路径要求 COPYUID、目标 UID/UIDVALIDITY 核验和 UID 定向 EXPUNGE。未声明垃圾箱/归档时解释原因，可显式选择目标目录。

`mail-actions` 和 `mail-actions-live` 通过。复用 GreenMail 验证原生 MOVE、复制、永久删除单封；在同一真实服务上从客户端屏蔽 MOVE 复验降级路径，屏蔽 MOVE 和 UIDPLUS 后明确拒绝且来源仍在。本地 SQLite 检查标签随移动、垃圾箱、归档、失效目标和撤销。实际删除仅用于本轮隔离夹具。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
