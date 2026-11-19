# 95 导出并重新映射账户规则和任务模板

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必要补充：配置迁移
Estimate: 2–3 人日
Requirements: REQ-09, REQ-11, REQ-17, REQ-18, SUP-01
Blocked by: 49, 78, 56

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户分享或恢复不含凭据的账户、规则、任务和插件配置模板，导入时映射目标环境。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 默认不包含 token/密码，schema 与插件版本明确，损坏文件拒绝并保留现状。
- [x] 账户/目录/alias 冲突、缺失插件与权限需逐项映射，未完成映射的自动化保持禁用。
- [x] 导入后预览与原模板语义一致，跨 Profile 不误引用同名账户。

## Blocked by

- [49 预览并导入 Outlook 在线账户配置](49-account-import.md)
- [78 审查自动化历史并从失败步骤继续](78-task-history.md)
- [56 导出并重新导入可搬运邮箱归档包](56-archive-export.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：配置模板导出使用白名单，检查内容摘要和文件版本，可逐项映射账户/目录、依赖任务、扩展版本、权限及 alias。原配置和模板映射在独立工作空间以同一 MIME 验证；新规则/任务/实例停用，插件权限不继承。settings-templates、task-plugins 通过。
