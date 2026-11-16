# 101 增加基于经典 Outlook 的第二 PST 导入器

Status: needs-info
Progress: not-started
Type: task
Scope: 可选扩展，未计入原始必做
Estimate: 2–3 人日
Requirements: OPT-01, REQ-07
Blocked by: 48, 53, 52

## Parent

[MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

在独立解析器之外，允许通过已安装经典 Outlook 读取 PST，作为可替换兼容路径。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 向导展示依赖 Outlook 的版本与位数，不隐藏额外 x86 组件要求。
- [ ] 使用同一保真样本对照，关闭导入后释放 Outlook/COM 资源。
- [ ] 无 Outlook 时可用的独立导入路径保持存在，不将第二路径作为绕过必做导入验收的手段。

## Blocked by

- [48 验证本机 Outlook 配置读取及位数边界](48-outlook-discovery.md)
- [53 在重启和插件停用后继续导入](53-import-resume.md)
- [52 完成 PST 复杂邮件的保真与异常报告](52-pst-fidelity.md)

## 外部条件与范围

是否增加 x86 桥或额外依赖需用户确认。

本项尚未选入首版，不能自行扩大必做范围。决策型任务完成后应生成细化实现任务，再实施所选能力。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
