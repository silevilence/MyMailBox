# 68 转换并预览可支持的 Outlook 规则

Spec: 000-mymailbox
Status: needs-info
Progress: in-progress
Type: task
Scope: 条件项：按规则导入评估结论
Estimate: 2–3 人日
Requirements: REQ-08
Blocked by: 54, 67, 49

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

把已验证的规则子集映射成本地规则，用户确认账户/目录及转换结果后启用。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 保留条件、例外、顺序、停止标志和可支持动作，显示逐项转换报告。
- [ ] 不支持脚本、企业条件或目标时整条规则默认禁用，不以简化后的宽条件启用。
- [ ] 转换前后用同一邮件样本对照；若可行性验证否定则交付明确限制和手动迁移指引。

## Blocked by

- [54 识别可转换的 Outlook 规则子集](54-rules-import-spike.md)
- [67 预览规则命中并解释执行失败](67-rule-preview.md)
- [49 预览并导入 Outlook 在线账户配置](49-account-import.md)

## 外部条件与范围

依赖 Outlook 规则可访问性；范围由验证结果限定。

本项按父规格的条件路线或评估结论决定是否实施；未选中时记录理由，不伪装为已实现。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：已接通经典 Outlook 清单转换、目标绑定、例外和动作逐项报告、同一批 MIME 的命中对照和停用草稿。outlook-rules、receive-rules、rule-preview 均通过；代表性真实 Outlook 规则的两端对照待验。
