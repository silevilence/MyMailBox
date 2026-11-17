# 02 启动并发布第一个插件化桌面应用

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-01, REQ-04
Blocked by: None

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

建立可构建的 Avalonia 应用，由最小内置插件显示欢迎页与版本，生成可双击的 win-x64 框架依赖产物。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] .NET 10 下构建、启动、关闭完整通过，欢迎能力不是宿主硬编码业务分支。
- [x] 实际发布产物非自包含、无 AOT/裁剪；验证有匹配系统 Runtime 可运行、缺失时可理解地失败。
- [x] 记录构建步骤、现有缓存复用和最小冒烟验证，固定 SDK 与初始依赖版本。

## Blocked by

None — 没有开发前置任务，可独立开始。

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实现与定向检查已完成：构建、欢迎插件注册/释放、框架依赖发布通过。见 [验收记录](../../../docs/validation/framework-plugins.md)。实际窗口启动/关闭及运行时缺失场景已在最终统一验收通过。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-16：统一验收完成，见 [验收记录](../../../docs/validation/framework-plugins.md)。
