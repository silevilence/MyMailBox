# 15 按本封或规则放行远程多媒体

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-22, REQ-19
Blocked by: 14

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户对被拦截内容选择本封加载，或设置受限的发件人/域规则，并能撤销。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 首次打开默认拦截；本封、发件人和域规则的范围可见，可预览和撤销。
- [x] 图片、音视频及其子请求都受策略约束，不仅检查 img；可信判断不使用显示名。
- [x] 离线、超大资源与失败显示可理解状态，放行不恢复脚本或宿主访问。

## Blocked by

- [14 默认隔离地显示 HTML 邮件](14-safe-html.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：IRemoteContentService 支持本封、实际发件地址、精确域规则的预览/持久化/撤销。宿主显示范围后显式放行，所有下载经受限 HTTP 客户端，拒绝重定向、脚本类型、本机/私网目标，单项 10 MiB、单封 30 MiB/30 项及 20 秒超时；已加载资源缓存 24 小时，撤销后缓存不再绕过策略。WebView 仍只读取隔离本地响应，CSS/字体/iframe 等子请求不放行。`dotnet run --project tests/MyMailBox.Checks --no-restore -- remote html` 通过默认零请求、作用域、伪造显示名、重启撤销、各多媒体类型、缓存与失败/取消检查；HTTP 测试使用内存响应，无重复在线下载。真实窗口验证集中执行。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
