# 52 完成 PST 复杂邮件的保真与异常报告

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-07, REQ-22
Blocked by: 51, 17, 16

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

完善 RTF、TNEF、嵌入邮件、附件、分类与历史日期的导入呈现，提供可核对结果。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 与样本源对比正文、附件字节/名称、目录、时间和已读状态；差异逐字段记录。
- [x] 损坏或受保护项目可跳过并报告，其他项目继续，非邮件内容不静默丢弃。
- [x] 样本回归可复用，不为测试重复生成或下载相同资源。

## Blocked by

- [51 导入 PST 目录和邮件并查看进度](51-pst-import.md)
- [17 修复常见乱码并提供手动阅读回退](17-encoding.md)
- [16 安全查看保存和打开附件](16-attachments.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：pst-fidelity 在产品入口导入既有 Outlook 样本。ANSI/Unicode 分别对照 3/35 个附件的名称、字节及 SHA-256，核对正文、HTML/CID、嵌入邮件、保留的原 RTF；CRC 错误仅影响一封，Unicode 联系人单独计数。传输头、地址、时间和 RTF 回退有逐项说明，分类映射到本地标签。样本未包含受保护邮件、分类或独立 TNEF 的真实客户端矩阵。
