# 05 热重载普通插件配置并保留上次有效状态

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-17
Blocked by: 04

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

普通插件使用独立配置文件，用户改配置或在设置页切换启用状态后即时生效，错误变更可恢复。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 核心和普通配置严格分离，凭据及邮件业务数据不混入插件配置。
- [x] 半写入、重复文件事件、schema 错误和多实例变化均经过验证后切换，错误保留上次有效配置。
- [x] GUI 与文件修改一致，版本化配置可迁移，错误指出具体字段并可重试。

## Blocked by

- [04 按核心 Profile 启动并恢复错误配置](04-core-profile.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

plugins.json 文件监听与 GUI 共用验证和切换路径；半写入、重复事件、schema 错误、双实例、初始化失败回退及 v0→v1 迁移定向检查通过。 完整回归与集中 UI 验收已完成，详见 [统一验收记录](../../../docs/validation/framework-plugins.md)。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-16：实现与定向检查完成。plugins.json 文件监听与 GUI 共用验证和切换路径；半写入、重复事件、schema 错误、双实例、初始化失败回退及 v0→v1 迁移定向检查通过。

- 2026-11-16：统一验收完成，见 [验收记录](../../../docs/validation/framework-plugins.md)。
