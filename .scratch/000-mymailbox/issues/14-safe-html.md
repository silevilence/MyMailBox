# 14 默认隔离地显示 HTML 邮件

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-22
Blocked by: 13

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

内置阅读器使用受控 WebView 展示 HTML/CID 内容，隔离邮件脚本与宿主能力。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 脚本、宿主对象、web message、表单、弹窗、自动下载和危险导航均被阻断。
- [x] 清理、CSP 与请求拦截覆盖 CSS/字体/图片/iframe/重定向，抓取证据显示默认无远程请求。
- [x] 纯文本回退、缩放和链接外部打开可用；阅读器不能访问编辑器高权限桥接。

## Blocked by

- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：IHtmlMailRenderer 清理 HTML 并生成 CSP，独立 WebView2 阅读控件关闭脚本/web message/host objects/表单对话框/权限/弹窗/下载；所有请求仅允许当前隔离文档和已声明 CID 资源，其余返回 403。宿主提供纯文本回退、缩放、显示目标 URL 后确认外部打开。复用本机缓存 WebView2 SDK 1.0.3179.45 与已装运行时。`dotnet run --project tests/MyMailBox.Checks --no-restore -- html mime` 通过清理/CSP/CID/导航样本，构建零警告。此处完成状态表示实现与定向检查通过；真实 WebView 请求取证与操作验收留待本轮最终集中执行，未宣称已取得网络抓取证据。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。 HTML 原生宿主清单与阅读空间缺陷已修复并定向复验；实际 5 页 A4 中文 PDF 首尾页及全文末段核对通过。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
