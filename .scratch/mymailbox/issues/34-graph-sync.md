# 34 增量同步 Graph 邮箱并恢复失效游标

Status: ready-for-agent
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-21, REQ-23
Blocked by: 32, 18

## Parent

[MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

保存各目录的增量同步状态，处理重命名、删除、限流和 delta token 失效。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 分页、delta 更新和删除后结果正确，多次运行不重复下载已有内容。
- [ ] 遵守限流退避，token 失效可重建并保持本地引用和独立归档。
- [ ] 离线和重连时界面可操作，显示同步范围与最后成功时间。

## Blocked by

- [32 登录 Outlook.com 并阅读云端邮件](32-graph-login.md)
- [18 在磁盘满和崩溃后恢复本地邮箱](18-storage-recovery.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
