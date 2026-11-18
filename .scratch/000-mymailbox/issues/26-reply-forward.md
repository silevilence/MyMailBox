# 26 回复全部和转发时保留上下文与身份

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-20, REQ-19
Blocked by: 23, 24, 25

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

从阅读界面回复、回复全部或转发，生成引用、附件选择及默认身份，保存已发送记录。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 回复优先使用原接收账户或显式替代，Reply-To、引用链和收件人去重正确。
- [x] 用户可折叠引用、移除附件并预览收件范围，不向未知 Bcc 自动扩散。
- [x] 发送状态与已发送目录一致，切换账户/重试不复制发送。

## Blocked by

- [23 使用富文本编辑并恢复草稿](23-composer.md)
- [24 在邮件中添加附件内联图片和签名](24-compose-assets.md)
- [25 持久化发件箱并处理不确定发送结果](25-outbox.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：本地阅读、统一收件箱、IMAP 与搜索结果可进入回复/回复全部/转发。优先原账户，需替代时显式确认；replies 与 outbox 验证 Reply-To、自身别名排除、可见收件人去重、密送隔离、引用链、转发附件及幂等提交。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
