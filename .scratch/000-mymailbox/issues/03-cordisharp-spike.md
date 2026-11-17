# 03 用 CordiSharp 加载并卸载一个外部插件

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-04, REQ-17
Blocked by: 02

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

外部测试插件贡献一个命令及事件订阅，主程序在不停机时装入、调用、停用并重新启用，验证直接复用路径。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 通过适配层复用 CordiSharp，记录采用版本/提交、许可及需补强点，不先另写框架。
- [x] 重复启停 30 次检查回调、计时器、引用与文件锁，无累积行为；失败可定位原因。
- [x] 宿主共享契约与插件私有依赖正确解析；不兼容包被拒绝，原应用可继续使用。

## Blocked by

- [02 启动并发布第一个插件化桌面应用](02-app-bootstrap.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

CordiSharp 0.2.0 适配、共享契约与私有依赖、30 轮 GC/事件/文件锁检查通过；详见 docs/cordisharp.md。 完整回归与集中 UI 验收已完成，详见 [统一验收记录](../../../docs/validation/framework-plugins.md)。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-16：实现与定向检查完成。CordiSharp 0.2.0 适配、共享契约与私有依赖、30 轮 GC/事件/文件锁检查通过；详见 docs/cordisharp.md。

- 2026-11-16：统一验收完成，见 [验收记录](../../../docs/validation/framework-plugins.md)。
