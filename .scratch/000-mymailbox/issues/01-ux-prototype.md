# 01 验证阅读、写信与自动化的交互原型

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: prototype
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-19, REQ-16
Blocked by: None

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用可交互原型演示多账户导航、选账户写信、跨账户自动化配置和失败恢复；形成可供后续任务引用的流程规范。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 使用模拟的工作、个人、本地账户完成四个完整场景，记录误选来源/目标的问题及修订。
- [x] 颜色之外同时有名称、地址/类型和路径标识；键盘主路径与加载/错误/空状态都有示例。
- [x] 仅借鉴现有客户端功能与心流，交付独立信息架构及页面说明，不以截图临摹代替设计。

## Blocked by

None — 没有开发前置任务，可独立开始。

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

已交付四个模拟交互场景与 docs/ux-flows.md；定向验证阅读身份、模拟草稿、跨账户范围、中断恢复和过期预览拦截。 完整回归与集中 UI 验收已完成，详见 [统一验收记录](../../../docs/validation/framework-plugins.md)。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-16：实现与定向检查完成。已交付四个模拟交互场景与 docs/ux-flows.md；定向验证阅读身份、模拟草稿、跨账户范围、中断恢复和过期预览拦截。

- 2026-11-16：统一验收完成，见 [验收记录](../../../docs/validation/framework-plugins.md)。
