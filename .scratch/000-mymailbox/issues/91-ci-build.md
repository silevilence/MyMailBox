# 91 在 GitHub Actions 构建并验证 NSIS 产物

Spec: 000-mymailbox
Status: ready-for-agent
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-24, REQ-02
Blocked by: 86, 90

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

建立 Windows CI，对提交执行构建检查、框架依赖发布与 NSIS 编译，产物留作检查而不自动公开 Release。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] CI 从指定提交构建，固定 SDK/依赖及 Actions 版本，所需检查失败则停止产出。
- [ ] 产物可启动且确认非自包含/无 AOT/无裁剪，记录文件与校验清单。
- [ ] 复用可用缓存，不重复下载测试资源；日志不泄露 Secrets，CI 构建与真实发布分开。

## Blocked by

- [86 使用 NSIS 安装卸载框架依赖应用](86-installer.md)
- [90 校验版本与提取对应 changelog 条目](90-release-metadata.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
