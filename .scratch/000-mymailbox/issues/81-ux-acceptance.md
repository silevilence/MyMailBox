# 81 完成键盘与无障碍的核心使用路径

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-19, REQ-16
Blocked by: 26, 63, 78, 80

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用完整读信、写信、选择目标、导入与自动化流程验收焦点、键盘、DPI 和错误恢复体验。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 键盘可完成主要操作，焦点可见；颜色不是唯一区别，控件有可读名称。
- [x] 不同窗口尺寸、高 DPI、长中文名称和大量目录不遮挡关键动作。
- [x] 加载/取消/空/错误状态一致，导入/自动化失败能回到可继续状态；修正原型偏差。

## Blocked by

- [26 回复全部和转发时保留上下文与身份](26-reply-forward.md)
- [63 在慢查询中更改条件并继续阅读](63-search-cancel.md)
- [78 审查自动化历史并从失败步骤继续](78-task-history.md)
- [80 集中管理同步提醒和任务通知](80-notifications.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：Windows 11 的实际发布程序通过 protocols 与默认 UI 集中验收。650×485 DIP、实际 192 DPI 下，Ctrl+F 聚焦搜索、Enter 查无结果、Esc 取消、F6 往返主导航、Ctrl+N/Tab 写信、保存反馈可见。键盘保存和恢复发件身份、修正无效 EML 路径后导入并阅读、修正自动化名称后重新预览，均在原窗口完成；验证超长中文目录名、分页及包含 50 个目录的基准工作区。证据在 artifacts/protocol-acceptance/ui-20261118-133619 与 artifacts/task000-final-ui。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
