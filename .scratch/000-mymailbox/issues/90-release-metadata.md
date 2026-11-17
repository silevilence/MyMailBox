# 90 校验版本与提取对应 changelog 条目

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-24
Blocked by: 02

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

提供可本地和 CI 复用的发布准备检查，读取 Tag 版本、项目版本及指定 changelog 段落。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] V/v 前缀归一化、合法版本校验、大小写重复 Tag 检查及版本不一致失败可验证。
- [x] 遍历发布耦合根/子项目，排除依赖版本；Release 正文只提取目标版本。
- [x] 以夹具验证首次仅新功能、后续分类与空段落处理；本任务不生成真实 changelog、不打 Tag 或发布。

## Blocked by

- [02 启动并发布第一个插件化桌面应用](02-app-bootstrap.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：scripts/check-release.ps1 校验严格 SemVer、V/v 归一化与重复、Prepare/Tagged 阶段、Tag 精确提交及干净工作区、根 Version 与全部 src 子项目的 MSBuild 有效 Version；依赖包版本不参与一致性比较。正文只提取对应 changelog 条目，首发仅新功能，空分类省略；可写 release-body.md 与 JSON 元数据。`pwsh -NoProfile -File scripts/test-release.ps1` 全部夹具通过。仅在临时仓库创建测试 Tag；未生成真实 changelog、未对本仓库打 Tag 或发布。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
