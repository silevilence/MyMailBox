# 25 持久化发件箱并处理不确定发送结果

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-20, SUP-01
Blocked by: 22, 18

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

邮件先进入可观察的发件箱，断网后重试；应用重启后继续处理未完成发送。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 待发、发送中、成功、部分失败、状态待确认有独立状态与记录。
- [x] 服务器接受但响应丢失时不盲目重发；已发送副本保存失败与实际发送失败区分。
- [x] 只重试可安全重试部分；取消未发项、崩溃恢复及幂等记录有验证。

## Blocked by

- [22 用发送账户完成一封纯文本邮件发送](22-smtp-send.md)
- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：发送操作先把原文、信封收件人和提交标识写入 SQLite。发件箱可查看当前账户的记录、投递历史和各收件人的结果，可取消待发、单独补存已发送副本或重试未获接收的收件人。离线连接每分钟重试，启动时继续待发任务；提交后中断则保留待确认状态。

`dotnet run --project tests/MyMailBox.Checks --no-restore -- outbox protocols protocol-faults` 通过。复用本机依赖与 TLS 故障服务，覆盖持久化幂等键、部分拒绝后仅重试剩余地址、DATA 回执丢失、取消及模拟崩溃后的恢复、断网后新运行器投递、本地副本写入故障和幂等补存。集中 UI 验收另记。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
