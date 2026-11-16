# 87 验证 NuGet 更新组件与 NSIS 的最小升级

Status: needs-info
Progress: not-started
Type: research
Scope: 必做验证
Estimate: 2–3 人日
Requirements: REQ-03
Blocked by: 86, 09

## Parent

[MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

比较 NetSparkle 与 Octokit 的适配范围，用两个本地测试版本完成检查到安装的可验证演示。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 优先验证现成包，明确 NetSparkle appcast/签名或 Octokit API 的责任边界，选定稳定版本。
- [ ] 不引入自包含或替换 NSIS；如不适配，记录原因后采用窄范围 HttpClient 后备。
- [ ] 到此任务才检查 Git remote 与项目配置中的仓库地址，缺失时提醒；本地测试源可继续验证非联网环节。

## Blocked by

- [86 使用 NSIS 安装卸载框架依赖应用](86-installer.md)
- [09 展示插件权限和可脱敏导出的诊断](09-plugin-diagnostics.md)

## 外部条件与范围

实施时才需要真实 GitHub owner/repo 和发布测试位置；当前不要求提供。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
