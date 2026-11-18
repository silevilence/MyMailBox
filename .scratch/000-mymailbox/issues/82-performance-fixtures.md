# 82 建立可复用的大邮箱验收数据集

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-23, SUP-01
Blocked by: 63, 75

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

生成或复用脱敏的 1 万/10 万封、多账户、中文 HTML 与大附件数据，提供可重复测量入口。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 数据分布、平均正文/附件大小、账户/目录数量和硬件条件记录可复现。
- [x] 分别测冷/热缓存、索引完成/未完成、后台同步开关，不用简单空邮件代替真实负载。
- [x] 数据集本地复用，GUI 验收前完成导入和准备，敏感真实邮件不进入仓库。

## Blocked by

- [63 在慢查询中更改条件并继续阅读](63-search-cancel.md)
- [75 按计划把超过 30 天的邮件归档到本地](75-archive-task.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：完成并复用 1 万/10 万封、多账户、四层目录和完整 MIME 数据。运行有/无本机 IMAP 并发、完整/未完成索引以及首次/热请求的八组测量。分布、机器、命令与首轮数据见 docs/validation/2026-11-18-performance-fixtures.md；首次请求保留 OS 文件缓存。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
