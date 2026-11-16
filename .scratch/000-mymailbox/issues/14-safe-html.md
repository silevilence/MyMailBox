# 14 默认隔离地显示 HTML 邮件

Spec: 000-mymailbox
Status: ready-for-agent
Progress: not-started
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

- [ ] 脚本、宿主对象、web message、表单、弹窗、自动下载和危险导航均被阻断。
- [ ] 清理、CSP 与请求拦截覆盖 CSS/字体/图片/iframe/重定向，抓取证据显示默认无远程请求。
- [ ] 纯文本回退、缩放和链接外部打开可用；阅读器不能访问编辑器高权限桥接。

## Blocked by

- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
