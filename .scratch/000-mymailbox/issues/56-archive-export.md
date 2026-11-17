# 56 导出并重新导入可搬运邮箱归档包

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-09, SUP-01
Blocked by: 19, 55

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

将选定邮箱/目录导出为版本化归档包，再导入到新位置，保留邮件和必要元数据。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 归档含 manifest、校验和、目录、标签与原文，损坏能检测。
- [x] 恢复到已有工作空间时处理 UUID/alias 冲突与引用映射。
- [x] 邮件归档与含全部设置的工作空间备份有明确区分，默认不导出凭据。

## Blocked by

- [19 备份并恢复完整工作空间](19-backup.md)
- [55 单封和批量导出原始 EML](55-eml-export.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：IMailArchive 与邮件归档包页面交付选定账户/目录范围导出和导入；版本化清单保存目录层级、账户说明/标签、邮件元数据、原文及 SHA-256，不带设置和凭据。导入在单个事务内验证，转换为独立本地账户；UUID 冲突重新映射并报告，alias 冲突自动采用显式 import 别名。`dotnet run --project tests/MyMailBox.Checks --no-restore -- archive` 通过范围、保真、标签、无凭据、无冲突保留 ID、冲突映射、损坏全事务回滚及取消检查。窗口验收集中执行。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
