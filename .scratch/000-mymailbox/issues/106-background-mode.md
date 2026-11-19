# 106 决定并验证完全退出 GUI 后的任务运行

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: research
Scope: 可选扩展决策，确认后继续拆分
Estimate: 2–3 人日
Requirements: OPT-03
Blocked by: 72, 11, 86

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

区分托盘仍运行与 GUI 完全退出，评估是否需要独立用户会话后台进程。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 明确睡眠/关机时不能常驻执行的事实和补跑策略，不默认引入 Windows 服务。
- [ ] 原型验证单实例调度、凭据访问、通知归属与应用升级停机协调。
- [ ] 若确认采用，再拆分后台进程与 UI 连接/安装管理任务；否则文档说明运行前提。

## Blocked by

- [72 恢复错过的调度并控制重叠与重试](72-scheduler-recovery.md)
- [11 管理账户身份及仅收仅发能力](11-accounts.md)
- [86 使用 NSIS 安装卸载框架依赖应用](86-installer.md)

## 外部条件与范围

需用户确认 GUI 完全退出后继续工作的需求。

本项尚未选入首版，不能自行扩大必做范围。决策型任务完成后应生成细化实现任务，再实施所选能力。

## 验证与交付记录

2026-11-19 本地评估已完成。已核对桌面单实例、凭据归属、调度补跑及升级停机；独立常驻进程留待范围决定。 此可选范围尚未选入首版。证据、限制与后续验收见[可选项本地评估](../../../docs/validation/2026-11-19-task-000-optional.md)。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
