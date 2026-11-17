# 55 单封和批量导出原始 EML

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-09
Blocked by: 13, 12

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户选择邮件与输出位置，导出可由其他客户端打开的 EML，显示逐项结果。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 尽可能保留原始 MIME；中文文件名、重名、路径长度和危险名称处理正确。
- [x] 完整附件可重新导入核对，未下载内容先获取或报告，不导出假完整文件。
- [x] 取消、磁盘满和部分失败可恢复；导出器通过统一格式契约注册。

## Blocked by

- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)
- [12 在写信与整理入口选择账户目录和身份](12-selectors.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：IMessageExporter 统一格式契约及宿主注册表已接入插件生命周期。阅读页支持当前页 Ctrl/Shift 多选导出 EML、逐项结果与取消；安全中文文件名、UUID 后缀、重名保留、相同内容复用、临时文件完成后发布。`dotnet run --project tests/MyMailBox.Checks --no-restore -- export mail` 验证原文字节/附件再导入、危险/保留/长路径名称、部分失败、取消与临时产物清理。磁盘/写入 IO 失败由同一逐项错误路径处理，已完成文件不回滚，重复执行可恢复。窗口验收集中执行。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
