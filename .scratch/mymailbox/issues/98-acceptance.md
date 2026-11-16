# 98 按原始需求逐项完成发布前验收

Status: needs-info
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-01, REQ-02, REQ-03, REQ-04, REQ-05, REQ-06, REQ-07, REQ-08, REQ-09, REQ-10, REQ-11, REQ-12, REQ-13, REQ-14, REQ-15, REQ-16, REQ-17, REQ-18, REQ-19, REQ-20, REQ-21, REQ-22, REQ-23, REQ-24
Blocked by: 81, 83, 84, 85, 52, 94, 97, 27, 28, 54, 57

## Parent

[MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

对需求覆盖矩阵逐条记录实现、测试证据、限制和阻塞，形成是否满足原始需求的结论。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 必做项全部有证据，公司实际 Exchange 路线、QQ/163、Outlook.com、POP3、配置/PST 导入不能仅凭通用接口勾选。
- [ ] 条件项记录采用/不采用及理由，未确认扩展不被伪装为完成，也不无故阻塞原始需求。
- [ ] 只在用户届时明确要求发布时执行 changelog→提交→Tag→push；此任务的完成不自动触发发布。

## Blocked by

- [81 完成键盘与无障碍的核心使用路径](81-ux-acceptance.md)
- [83 达到长列表搜索阅读和后台任务性能目标](83-performance.md)
- [84 联调各提供者的故障与恢复矩阵](84-reliability.md)
- [85 受控地自动回复转发与调用外部动作](85-automatic-replies.md)
- [52 完成 PST 复杂邮件的保真与异常报告](52-pst-fidelity.md)
- [94 验证每一类内置能力都可被外部实现替换](94-extensibility-audit.md)
- [97 完成安装迁移与故障恢复的使用指引](97-user-guides.md)
- [27 用可替换地址建议完成收件人输入](27-address-suggestions.md)
- [28 安排稍后发送并取消尚未发送的邮件](28-scheduled-send.md)
- [54 识别可转换的 Outlook 规则子集](54-rules-import-spike.md)
- [57 将邮件导出为 PDF HTML 和纯文本](57-readable-export.md)

## 外部条件与范围

依赖实际公司路线任务、各授权账户实测，以及已确认范围；见总览的条件依赖门槛。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

条件门槛：公司邮箱门槛与可靠性联调相同；规则导入必须有评估结论，结论可行且纳入范围时还需完成转换任务。可选项只有明确采用后才加入验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
