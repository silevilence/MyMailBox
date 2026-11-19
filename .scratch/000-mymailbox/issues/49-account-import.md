# 49 预览并导入 Outlook 在线账户配置

Spec: 000-mymailbox
Status: needs-info
Progress: in-progress
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-06
Blocked by: 48, 11, 12

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

导入向导列出可发现账户和连接字段，用户选择后生成本应用账户并完成重新授权。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 逐字段展示已导入、需补充、不支持及来源信息，支持显示名/标签与重复账户映射。
- [x] 取消不创建半成品账户；密码/token 不作为跨客户端无损搬运承诺。
- [ ] 导入的账户可接收或发送并保存稳定引用；仅显示邮箱地址的演示不算验收完成。

## Blocked by

- [48 验证本机 Outlook 配置读取及位数边界](48-outlook-discovery.md)
- [11 管理账户身份及仅收仅发能力](11-accounts.md)
- [12 在写信与整理入口选择账户目录和身份](12-selectors.md)

## 外部条件与范围

需支持版本 Outlook 和授权测试账户。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：新增经典 Outlook 16 Internet 账户预览向导。仅在用户选择 Profile 后读取白名单字段，读取到的主机、端口、登录名可预填，TLS 和缺失项需核对，用户可编辑显示名及标签或显式映射到同地址、同类型账户。账户与协议设置在同一事务中创建，凭据需在现有协议页重新授权。outlook-import、accounts、protocols 定向通过。现有 Outlook 样本的 Accounts=0；注册表字段兼容性、有账户的真实读取和重新授权收发待验。

- 11 月 19 日集中验收在 GreenMail 回环服务上使用导入服务创建 IMAP/SMTP 账户，从数据库重读其端点，补录测试凭据后完成实际收发及认证失败恢复。源 Profile 可发现字段及服务商授权仍需包含账户的 Outlook 测试环境。

- 2026-11-19：账户预览逐字段列出来源、已读取值及需补充项，分开标注收发服务器、端口、身份、加密方式，并说明 POP3 的 SMTP 与未知提供者。outlook-import 通过缺字段、回执和重复映射。protocol-live 再次从导入账户通过 GreenMail TLS 收发并按原 ID 读取；经典 Outlook 有在线账户的实际 Profile 仍待验。
