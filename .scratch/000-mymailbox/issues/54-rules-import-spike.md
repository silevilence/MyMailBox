# 54 识别可转换的 Outlook 规则子集

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: research
Scope: 必做验证
Estimate: 2–3 人日
Requirements: REQ-08
Blocked by: 48

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

只读枚举规则及条件/动作/例外/顺序，形成转换矩阵和样本，不先承诺完整迁移。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 区分服务器规则和客户端规则，记录常见字段、目标目录和特殊动作可访问性。
- [ ] 未知条件、脚本或失效目标都有原始描述，不因无法识别而丢弃。
- [ ] 明确可实现子集、无法转换项及后续任务输入；若接口不可用形成明确结论和替代指引。

## Blocked by

- [48 验证本机 Outlook 配置读取及位数边界](48-outlook-discovery.md)

## 外部条件与范围

需要允许读取规则的 Outlook Profile 与代表性规则样本。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
