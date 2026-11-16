# 04 按核心 Profile 启动并恢复错误配置

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-17
Blocked by: 03

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

提供默认 GUI Profile，加载核心服务组合；手改核心配置只在下一次启动生效，错误时能进入修复/安全启动入口。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 核心清单、实例与配置只在启动读取，运行时编辑不会偷偷更换核心服务。
- [x] 循环依赖、缺失服务、版本不兼容均展示依赖链和修复位置。
- [x] 核心与普通、内置与外部分别建模；用一个外部核心和一个内置普通插件证明不混同。

## Blocked by

- [03 用 CordiSharp 加载并卸载一个外部插件](03-cordisharp-spike.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

profiles/default/core.json 启动快照与安全修复入口已实现；外部核心、内置普通、循环链、缺失版本/服务与配置损坏定向检查通过。 实际 UI 与完整回归按本次任务约定集中验收，发现未解决问题时回退状态。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-16：实现与定向检查完成。profiles/default/core.json 启动快照与安全修复入口已实现；外部核心、内置普通、循环链、缺失版本/服务与配置损坏定向检查通过。
