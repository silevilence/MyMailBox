# 77 通过事件手动操作和前置任务触发自动化

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-12, REQ-13, REQ-15
Blocked by: 72, 66, 76

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

支持同步完成、网络恢复、启动、手动及其他任务完成触发，并对事件风暴去重。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 收件、发送完成、同步完成、启动、解锁/恢复、网络恢复、手动及其他任务完成可注册触发；手动执行可选预览。
- [x] 任务依赖环、重复事件、因果循环和连锁触发有检测与上限。
- [x] 任务触发可按前次成功/失败筛选，事件来源与时间在历史中可追踪。

## Blocked by

- [72 恢复错过的调度并控制重叠与重试](72-scheduler-recovery.md)
- [66 确保正文规则和重试不会重复产生副作用](66-rule-stages.md)
- [76 在任务中组合复制标记导出和提醒动作](76-task-actions.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：task-events 与 task-events-live 通过，真实 IMAP 新到达、同步及 SMTP 投递回执均触发已保存动作。任务成功/失败筛选、手动执行、启动风暴合并和依赖环拒绝通过。Windows 解锁/恢复绑定窗口通知，网络恢复绑定网络可用性事件；集中 UI 脚本使用隔离窗口消息验证恢复入口。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
