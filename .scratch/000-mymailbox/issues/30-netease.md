# 30 通过 163 预设完成收发闭环

Spec: 000-mymailbox
Status: needs-info
Progress: in-progress
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-05, REQ-20, REQ-21
Blocked by: 21, 26

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

提供 163 账户预设、授权说明及所需客户端识别适配，完成真实收发。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 协议开通、授权码及厂商拒绝信息转为可操作提示。
- [ ] 真实账户收发、中文目录、正文/附件和重连通过，不硬编码未经验证的私有行为。
- [x] 复用通用协议层并保持厂商预设可替换，不复制一整套同步引擎。

## Blocked by

- [21 增量接收 IMAP 邮件并离线阅读](21-imap-sync.md)
- [26 回复全部和转发时保留上下文与身份](26-reply-forward.md)

## 外部条件与范围

实际验收需授权的 163 测试账户。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：新增可独立启停的网易 163 普通预设，复用现有 IMAP/SMTP。预设填入隐式 TLS 参数，并提示开通客户端服务、生成授权密码和检查网页版安全限制。服务器声明 ID 扩展时发送 MyMailBox 自身名称/版本。netease-preset 覆盖预设生命周期、账户和缓存保留、连接绑定，并通过本机 TLS IMAP 端点验证只有服务端声明 ID 时才发送真实产品标识、认证拒绝会显示授权指引；实际厂商账户及客户端识别的线上响应尚未验证。参数见 [Thunderbird 自动配置](https://autoconfig.thunderbird.net/v1.1/163.com)，授权流程见 [网易邮箱帮助](https://help.mail.126.com/faqDetail.do?code=d7a5dc8471cd0c0e8b4b8f4f8e49998b374173cfe9171305fa1ce630d7f67ac2ed007f2b27412aae)。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
