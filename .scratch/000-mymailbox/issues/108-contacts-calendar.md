# 108 界定联系人和日历是否扩展为独立产品功能

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: research
Scope: 可选扩展决策，确认后继续拆分
Estimate: 2–3 人日
Requirements: OPT-02
Blocked by: 49, 52, 27

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

确认 PST 非邮件项目和通讯录建议以外的联系人/日历同步是否进入范围。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 当前 PST 非邮件项目已经报告，不因暂不支持而静默遗漏。
- [ ] 如需日历/联系人，列出提供者能力、冲突/时区/邀请等新增需求并另建任务集。
- [ ] 未确认前不将完整 Outlook 日历替代加入首版验收。

## Blocked by

- [49 预览并导入 Outlook 在线账户配置](49-account-import.md)
- [52 完成 PST 复杂邮件的保真与异常报告](52-pst-fidelity.md)
- [27 用可替换地址建议完成收件人输入](27-address-suggestions.md)

## 外部条件与范围

需用户确认是否扩展产品范围。

本项尚未选入首版，不能自行扩大必做范围。决策型任务完成后应生成细化实现任务，再实施所选能力。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
