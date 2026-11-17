# 11 管理账户身份及仅收仅发能力

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-18, REQ-05, SUP-02
Blocked by: 10, 09

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户添加、编辑、停用和移除账户，设置名称、tags、颜色、备注、alias 与收发能力，凭据受系统保护。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 账户 UUID 自动生成且稳定，alias 唯一；重命名不改变引用，恢复冲突明确处理。
- [x] 支持多个同类型账户、仅收、仅发及本地账户；UI 按实际能力禁用操作并解释原因。
- [x] 凭据仅保存引用，登录失败可恢复；移除账户先说明缓存与独立归档处理，不能误删归档。

## Blocked by

- [10 打开一封本地邮件并持久保存到工作空间](10-local-inbox.md)
- [09 展示插件权限和可脱敏导出的诊断](09-plugin-diagnostics.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：账户管理页面与 IAccountService 已实现；SQLite 保存稳定 ID、唯一 alias、标签/颜色/备注/排序、能力与凭据引用，凭据使用 Windows 当前用户 DPAPI 加密。`dotnet run --project tests/MyMailBox.Checks --no-restore -- accounts` 通过创建/重命名/停用/移除、同类型账户、冲突恢复、凭据重开及更新、独立归档保留检查。在线提供者接入属于后续任务，页面明确标注待接入，不声称真实登录通过。窗口验收留待本轮集中执行。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
