# 22 用发送账户完成一封纯文本邮件发送

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-05, REQ-20
Blocked by: 11, 12, 13

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户选择有发送能力的账户填写 To/Cc/Bcc 和正文，SMTP 插件完成发送并展示结果。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 仅发送账户可用、仅接收账户不可用；收发服务器及认证可独立配置。
- [x] 地址校验、TLS/认证失败、服务器拒绝和部分收件人失败均有可理解反馈。
- [x] 服务器允许的发件身份才可选择，Bcc 不泄露到不应包含它的邮件头。

## Blocked by

- [11 管理账户身份及仅收仅发能力](11-accounts.md)
- [12 在写信与整理入口选择账户目录和身份](12-selectors.md)
- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：已实现独立 SMTP 配置、纯文本 To/Cc/Bcc、账户能力限制、服务器拒绝/部分接收/结果不明反馈。Bcc 仅用于信封，发送不自动重试。构建及地址/能力检查通过；GreenMail 与故障服务、桌面集中验收待本轮末执行，保持 in-progress。

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-17：用户确认本轮没有授权的 IMAP/SMTP/POP3 测试账户，也没有 Outlook.com 应用注册与测试环境；真实协议集成验收阻塞，继续独立任务。

- 2026-11-17：用户授权本地 GreenMail、只读 Outlook 发现及独立无账户 Profile/PST；开始实施，最终集中验收。32/35 暂缓。

- 2026-11-17 集中验收：GreenMail 实际 To/Cc/Bcc 纯文本及桌面发送通过；受控 TLS 服务验证发件/收件拒绝、部分接收和 DATA 确认丢失，Bcc 原文不泄露，不自动重发。 证据见 [协议与 Outlook/PST 验收](../../../docs/validation/2026-11-17-protocol-outlook.md)。
