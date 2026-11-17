# 12 在写信与整理入口选择账户目录和身份

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-16, REQ-18, REQ-19
Blocked by: 11, 06

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

交付复用的账户/目录/发送身份选择器，按搜索、标签和最近使用选目标，持久化为稳定引用。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 名称、地址、类型、颜色、标签和目录路径清晰，支持多选、目录树及特殊文件夹。
- [x] 按所需能力筛选并解释不可选项，普通用户无需输入 UUID。
- [x] 固定集合与动态标签集合明确区分；目录重命名后引用仍有效，删除后显示失效引用。

## Blocked by

- [11 管理账户身份及仅收仅发能力](11-accounts.md)
- [06 通过普通插件提供并替换页面和选择器](06-ui-contributions.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：公开 ISelectionService 与内置 mail-selectors 插件提供账户、目录树和发送身份选择；搜索、标签、最近使用、固定/动态集合、能力说明与稳定引用持久化均已实现。`dotnet run --project tests/MyMailBox.Checks --no-restore -- selectors` 验证重命名、删除失效、重启、动态标签变化及能力拒绝。写信入口保存一个发送身份，整理入口支持多选；实际邮件发送和动作执行由后续任务交付。窗口验收留待本轮集中执行。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
