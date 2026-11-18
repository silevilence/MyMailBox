# 27 用可替换地址建议完成收件人输入

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-20, REQ-16
Blocked by: 23, 11

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

提供本地联系人和历史收件人建议，用户可选择、编辑和移除建议。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 姓名与地址可区分，重复和无效地址可修正，To/Cc/Bcc 都适用。
- [x] 建议查询异步可取消，错误不会阻止手动输入。
- [x] 外部建议插件可替换，历史记录可清理，默认不扩展为完整云通讯录同步。

## Blocked by

- [23 使用富文本编辑并恢复草稿](23-composer.md)
- [11 管理账户身份及仅收仅发能力](11-accounts.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：提供账户隔离的本地联系人、已接收 SMTP 地址历史、可取消的外部建议，以及 To/Cc/Bcc 校正去重和历史清理。address-book 用独立加载的外部插件验证替换/取消/卸载；compose-live 验证配置的备用身份和成功投递才记录的建议。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
