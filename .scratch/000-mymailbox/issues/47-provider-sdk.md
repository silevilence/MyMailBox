# 47 使用外部邮箱插件验证统一提供者契约

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-05, REQ-04
Blocked by: 33, 40, 45, 46

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

提供一个不同实现的测试邮箱插件，在同一 UI 中完成账户添加、读写、搜索能力探测与动作。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 契约覆盖认证、目录、增量、原文流、发送、上传/移动、标记、配额和服务端搜索，能力可选。
- [x] 外部新类型无需修改宿主，缺失或卸载后账户仍保留并指示依赖。
- [x] 共享协议测试可复用到 QQ/163/Graph/实际 Exchange，不依赖具体插件类型强转。

## Blocked by

- [33 通过 Graph 发送并处理邮件状态](33-graph-send.md)
- [40 自动接收多个账户并展示统一同步状态](40-multi-sync.md)
- [45 恢复跨邮箱操作的每个中断点](45-move-recovery.md)
- [46 处理多客户端修改删除和目录冲突](46-sync-conflicts.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：新增可卸载的邮箱提供者 SDK 和独立回环插件。mailbox-provider 检查通过安装授权、同账户投递、目录/原文、搜索、上传移动标记、配额、统一收件箱增量与重启恢复。共享只读探针可接本地、IMAP 和其他实现。FlaUI 场景随本轮集中执行。
