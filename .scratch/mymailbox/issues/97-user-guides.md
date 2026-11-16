# 97 完成安装迁移与故障恢复的使用指引

Status: ready-for-agent
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-19, REQ-02, REQ-06, REQ-07, SUP-01
Blocked by: 53, 56, 78, 93, 95, 96

## Parent

[MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

将实际可用功能写成首次设置、Outlook 迁移、自动归档、插件管理和更新恢复指南，入口可在应用中找到。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 首次运行可完成添加账户/导入/重新授权，说明支持版本和损失报告位置。
- [ ] 文档与实际 UI、配置/备份格式一致，示例不含真实凭据或固定假仓库。
- [ ] 插件开发和贡献说明链接正确；发布说明遵循 AGENTS 与 generate-changelog，不预写尚未实现功能已完成。

## Blocked by

- [53 在重启和插件停用后继续导入](53-import-resume.md)
- [56 导出并重新导入可搬运邮箱归档包](56-archive-export.md)
- [78 审查自动化历史并从失败步骤继续](78-task-history.md)
- [93 验证从已安装旧版到 Actions 新版的升级](93-update-e2e.md)
- [95 导出并重新映射账户规则和任务模板](95-settings-transfer.md)
- [96 在托盘运行期间持续收件并执行任务](96-tray-lifetime.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
