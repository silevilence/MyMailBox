# 65 新邮件到达时执行可配置规则

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-11, REQ-21
Blocked by: 60, 42, 40

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户建立按账户/目录、发件人/收件人/主题条件匹配的新收件规则，执行标记或移动。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 规则通过编辑器保存、启停、排序并在接收链路执行，显示命中和动作结果。
- [x] 支持条件、例外、多个动作及停止后续规则，语义复用统一条件树。
- [x] 默认不把历史导入或初次全量同步当新邮件，用户可明确开启历史处理。

## Blocked by

- [60 组合 AND OR NOT 与字符串正则条件](60-query-language.md)
- [42 在同一邮箱内复制移动和删除邮件](42-same-store-actions.md)
- [40 自动接收多个账户并展示统一同步状态](40-multi-sync.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：新增规则编辑、版本与排序，IMAP 增量、POP3 收取及明确开启的本地历史导入进入持久队列；条件与例外使用搜索的三值语义。receive-rules、imap-sync、imap-conflicts 通过，真实收件后加星与离线标记一致，验证多动作、移动与停止后续规则。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
