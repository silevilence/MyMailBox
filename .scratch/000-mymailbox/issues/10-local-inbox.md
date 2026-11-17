# 10 打开一封本地邮件并持久保存到工作空间

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-05, SUP-01, REQ-23
Blocked by: 06

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

通过内置本地邮箱插件导入一封 EML，显示摘要和纯文本，重启后仍可浏览。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 原始 MIME、稳定邮件 ID、目录与基础元数据持久化，正文按需读取。
- [x] 邮件和目录显示由插件服务提供，提供第二测试存储实现验证接口。
- [x] 解析或写入失败不留下可见的半封邮件，列表仅加载可见页。

## Blocked by

- [06 通过普通插件提供并替换页面和选择器](06-ui-contributions.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：已交付内置本地邮箱插件、宿主阅读页面、SQLite 原文/摘要原子存储、50 封分页和按需纯文本读取。`dotnet run --project tests/MyMailBox.Checks --no-restore -- mail` 通过重开、重复 Message-ID、分页、失败回滚、取消、第二存储实现与插件生命周期检查。真实窗口验收留待本轮集中执行。当前导入上限为 25 MiB；HTML 和完整 MIME 展示由后续工单交付。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
