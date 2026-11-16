# 50 用真实 PST 样本验证解析库和保真范围

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: research
Scope: 必做验证
Estimate: 2–3 人日
Requirements: REQ-07
Blocked by: 02

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

比较可用 PST 解析路径，读取典型目录、原文和附件，确定稳定依赖及已知损失。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 覆盖 ANSI/Unicode、大文件、嵌套目录、RTF/HTML、嵌入邮件和异常项目样本。
- [ ] 记录 Windows x64 构建、许可/分发条件、吞吐和内存；不因有格式规范就承诺完整实现。
- [ ] 输出可采用库及限制，商业方案需要预算决定；原始 PST 始终保留。

## Blocked by

- [02 启动并发布第一个插件化桌面应用](02-app-bootstrap.md)

## 外部条件与范围

需要去敏 PST 样本；如仅商业方案满足需求，需实施时确定预算。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
