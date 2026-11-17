# 69 创建一次性提醒并可靠通知

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-12, REQ-15
Blocked by: 12, 18, 06

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户为邮件或自定义事项设置时刻与通知方式，到时提醒并能延后或取消。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 重启后计划仍存在，关联邮件移动/删除时有可理解状态。
- [x] 时区和设备运行前提明确，通知可关闭或延后，不反复弹同一提醒。
- [x] 通知与调度均通过插件契约实现，测试时可使用虚拟时钟。

## Blocked by

- [12 在写信与整理入口选择账户目录和身份](12-selectors.md)
- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)
- [06 通过普通插件提供并替换页面和选择器](06-ui-contributions.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：实现普通提醒插件、持久计划与幂等通知 outbox、应用内横幅/静默、关闭/取消/延后、指定时区和稳定邮件引用。IReminderService/INotificationSink 契约可替换，TimeProvider 可注入。dotnet build 和 reminders 定向检查通过，覆盖虚拟时钟、重启补一次、投递确认中断不重复、关联邮件移动/删除/账户移除、夏令时歧义拒绝及停止循环释放。docs/scheduling.md 明确 GUI/插件运行前提。真实提醒窗口和横幅待本轮末集中验收。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
