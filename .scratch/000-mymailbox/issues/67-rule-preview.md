# 67 预览规则命中并解释执行失败

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-11, REQ-19, SUP-02
Blocked by: 66, 62

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户保存前试运行，查看命中样本、例外原因和动作影响，之后可按失败项恢复。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 预览无写副作用，范围、数量、目标和破坏性操作明确显示。
- [x] 执行历史能解释不匹配/未知/失败，禁止把未支持条件丢弃后继续执行。
- [x] 规则权限与账户能力变化后暂停并提示；能撤销与不能撤销的动作分开。

## Blocked by

- [66 确保正文规则和重试不会重复产生副作用](66-rule-stages.md)
- [62 用可视化条件构建并保存高级搜索](62-search-builder.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：保存前可试运行当前草稿，显示范围、样本、例外、未知及移动目标。只读 SQLite 连接配合临时元数据，rule-preview 验证冷缓存预览前后 data_version 未变、账户停用暂停、恢复后重试及本地移动撤销。search-extension 验证真实外部扩展的只读预览。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
