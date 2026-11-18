# 64 通过外部插件增加搜索字段和运算符

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-10, REQ-04
Blocked by: 60, 61, 07

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

示例插件贡献一个派生字段和条件，进入搜索 UI、索引及查询执行，并能卸载。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 扩展标识带命名空间和版本，契约覆盖字段、运算符、规划、索引/抽取和结果高亮。
- [x] 高成本扩展先缩小候选并批处理，不能逐封阻塞 UI。
- [x] 卸载后保存查询保留且报告缺失能力，自动化引用暂停，不静默忽略条件。

## Blocked by

- [60 组合 AND OR NOT 与字符串正则条件](60-query-language.md)
- [61 搜索中文和多语言正文内容](61-fulltext.md)
- [07 在插件管理页安装启用和移除插件包](07-plugin-packages.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：真实加载 ExamplePlugin 的 search-case 入口，版本化派生字段及 number 运算符进入字段列表。search-extension 验证候选裁剪、缓存和高亮、实际卸载后的未知语义、已保存引用及取消账户读取授权后的隔离。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
