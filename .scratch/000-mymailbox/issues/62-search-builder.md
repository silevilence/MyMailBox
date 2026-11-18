# 62 用可视化条件构建并保存高级搜索

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-10, REQ-14, REQ-19
Blocked by: 60, 61, 12

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户通过条件编辑器或语法输入筛选，保存为可复用搜索，并在自动化中引用。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 可表达部分双向转换语义一致，复杂扩展无法转换时保留原表达式并说明。
- [x] 保存搜索绑定稳定账户/目录，重命名不失效，缺失引用可修复。
- [x] 条件组件按字段类型约束输入，键盘操作与自然语言摘要可用。

## Blocked by

- [60 组合 AND OR NOT 与字符串正则条件](60-query-language.md)
- [61 搜索中文和多语言正文内容](61-fulltext.md)
- [12 在写信与整理入口选择账户目录和身份](12-selectors.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：新增可操作的嵌套条件编辑器、语法回读、键盘载入、字段类型输入与中文摘要。保存搜索以稳定 GUID 绑定范围，供自动化按 ID 求值；search-builder 验证重开、改名、范围失效、修复及扩展表达式保留。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
