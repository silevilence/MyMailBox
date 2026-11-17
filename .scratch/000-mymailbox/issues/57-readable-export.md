# 57 将邮件导出为 PDF HTML 和纯文本

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-09
Blocked by: 14, 55

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户在阅读或批量导出中选择阅读型格式并预览结果，保留必要头部和正文。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] PDF 分页可读、中文正常，HTML 资源规则与安全阅读一致，不自动联网取追踪资源。
- [x] 说明阅读格式不等同完整邮件备份；附件是否另存由选项明确。
- [x] 三个格式均能通过独立导出器注册，批量取消及命名冲突一致。

## Blocked by

- [14 默认隔离地显示 HTML 邮件](14-safe-html.md)
- [55 单封和批量导出原始 EML](55-eml-export.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：PDF/HTML/TXT 独立注册，保留必要头部与正文，明确阅读副本/完整备份差异，支持附件另存及系统应用结果预览。HTML 使用相同清理器、内嵌 CID 与禁联网 CSP；PDF 使用隔离 WebView2、A4、中文字体/分页样式及原子文件发布。dotnet build 与 readable-export 定向测试通过，覆盖三格式契约、中文头部/CID/安全 HTML、附件选项、重名/失败/取消。PDF 控制台测试使用记录打印器，不能替代实际中文及分页验收；真实 WebView2/PDF 验收将在本轮末集中执行，未声称已通过。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。 HTML 原生宿主清单与阅读空间缺陷已修复并定向复验；实际 5 页 A4 中文 PDF 首尾页及全文末段核对通过。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
