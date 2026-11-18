# 60 组合 AND OR NOT 与字符串正则条件

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-10, REQ-11, REQ-13, REQ-14
Blocked by: 59

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

高级搜索解析为统一版本化条件树，支持分组、精确/包含/开头/结尾/正则、日期及存在条件。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 给出语法提示、错误位置和转义规则，AND/OR/NOT 优先级与嵌套有确定测试。
- [x] 空值、正文未下载、插件缺失使用未知/不完整语义，NOT 不会把未知误变成可删除目标。
- [x] 正则有超时和取消，参数化查询，无任意 SQL/代码执行；搜索与自动化共用语义。

## Blocked by

- [59 按邮件字段搜索并定位结果](59-search-fields.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：新增版本化条件树和高级输入，搜索与自动化按同一编译器判断真、假、未知。search-conditions 与 search-fields 通过，覆盖优先级、括号、转义、错误位置、空值取反、缺失插件暂停、正则及取消。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
