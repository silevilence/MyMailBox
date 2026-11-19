# 36 通过本地 Exchange 提供者浏览并读取邮件

Spec: 000-mymailbox
Status: needs-info
Progress: in-progress
Type: task
Scope: 条件路线：需要本地 Exchange/EWS 时
Estimate: 2–3 人日
Requirements: REQ-05, REQ-21
Blocked by: 35, 12, 13

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

按已确认认证方式提供本地 Exchange 账户添加、目录发现、分页读取及附件获取。

通用产品约束：服务器地址及登录身份由用户按账户输入（或受控发现），不得写死验收公司的域名。2026-11-17 已确认测试端点的 EAS OPTIONS 能力查询成功，EWS 仍未通过认证；EAS 如被最终选定，应先补充对应实现工单及设备策略/能力边界，不将 OPTIONS 成功算成本项阅读功能完成。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 端点发现和重定向限制合理，证书或认证错误可诊断，不关闭证书校验凑通过。
- [ ] 公司测试邮箱目录、中文内容、正文和附件正确，接入不依赖 Outlook 必须运行。
- [ ] 实现继承统一账户/阅读契约；若发现阶段选定非 EWS 路线，记录对应替代工单而不是做无用分支。

## Blocked by

- [35 验证公司 Exchange 的真实接入路径](35-exchange-discovery.md)
- [12 在写信与整理入口选择账户目录和身份](12-selectors.md)
- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)

## 外部条件与范围

依赖公司授权环境和接入结论。

本项按父规格的条件路线或评估结论决定是否实施；未选中时记录理由，不伪装为已实现。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：实现公司 Exchange 的候选 ActiveSync 路线。每账户独立 HTTPS 端点、登录身份、DPAPI 凭据和设备同意；WBXML 目录层级、分页、完整 MIME/附件与离线缓存已通过本地协议样本。EWS 与公司服务器实际访问仍待验证。
