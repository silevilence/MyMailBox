# 17 修复常见乱码并提供手动阅读回退

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-22
Blocked by: 13, 14

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

通过中文与多语言样本完善编码、坏 HTML、RTF/TNEF 内容的读取与回退。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] UTF-8、GB18030/GBK、Big5、日文、错误 charset、编码主题均有验证样本和结果。
- [x] 支持手动编码、纯文本和原文切换，不能修复的内容明确标识。
- [x] TNEF/winmail.dat、RTF 和损坏 MIME 不崩溃且不静默丢附件，扩展处理器可替换。

## Blocked by

- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)
- [14 默认隔离地显示 HTML 邮件](14-safe-html.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：阅读器注册系统代码页，支持自动编码及 UTF-8、GB18030/GBK、Big5、Shift-JIS、Windows-1252 手动正文覆盖，保留纯文本/原文切换。未知 charset 与替代字符明确提示。RTF/TNEF 未转换时报告限制并在附件入口保留可保存的正文.rtf/winmail.dat，不伪称已展开其附件；IMimeContentDecoder 可替换处理。`dotnet run --project tests/MyMailBox.Checks --no-restore -- encoding` 覆盖各语言、错误声明、编码主题、坏 HTML、损坏 MIME 和第二处理器。窗口验收集中执行。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
