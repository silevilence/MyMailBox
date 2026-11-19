# 38 持续同步公司 Exchange 并恢复断网

Spec: 000-mymailbox
Status: needs-info
Progress: in-progress
Type: task
Scope: 条件路线：需要本地 Exchange/EWS 时
Estimate: 2–3 人日
Requirements: REQ-21, REQ-23
Blocked by: 36, 18

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

实现公司 Exchange 的增量、通知或轮询和离线恢复，接入统一同步状态。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 在服务器允许范围内选同步方式，断线、同步令牌失效及服务器限制可恢复。
- [x] 目录/邮件变化最终一致，重启不反复全量下载。
- [ ] 后台同步时主界面可继续阅读和编辑，日志不包含凭据及正文。

## Blocked by

- [36 通过本地 Exchange 提供者浏览并读取邮件](36-ews-read.md)
- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)

## 外部条件与范围

依赖公司授权环境。

本项按父规格的条件路线或评估结论决定是否实施；未选中时记录理由，不伪装为已实现。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：候选 ActiveSync 同步已接入统一接收。分页和游标持久化，重开继续，失效键使用独立暂存目录快照并原子切换，保留已下载原文。exchange-eas 本地协议样本通过，真实服务器的长时运行待验。
