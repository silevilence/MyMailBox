# 48 验证本机 Outlook 配置读取及位数边界

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: research
Scope: 必做验证
Estimate: 2–3 人日
Requirements: REQ-06, REQ-07
Blocked by: 02

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

发现经典/新 Outlook、Profile 和 Office 位数，读取可用账户配置及 PST 位置，形成兼容证据。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 区分 OOM、Account Management API 和 MAPI 所能读取字段，不把地址枚举当完整配置导入。
- [x] 在 x64 交付限制下明确 32 位 Outlook 可行路径；若需额外 x86 桥，先形成待决定项。
- [x] 报告支持版本、读取限制及重新授权需求，不读取/记录不必要的秘密或把新 Outlook 当经典 COM。

## Blocked by

- [02 启动并发布第一个插件化桌面应用](02-app-bootstrap.md)

## 外部条件与范围

需授权的 Outlook 安装与样本 Profile；在实施时确认版本和位数。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-17：用户确认目前没有对应真实测试环境/样本，先推进独立任务。真实集成验收阻塞，保持验收项未勾选。

- 2026-11-17：用户授权本地 GreenMail、只读 Outlook 发现及独立无账户 Profile/PST；开始实施，最终集中验收。32/35 暂缓。

- 2026-11-17 集中验收：经典 Outlook 16.0.20527.20196 x64 与新 Outlook 1.2026.1103.200 只读发现；实际无账户 Profile 的 OOM 会话与 PST 路径操作通过，测试结束恢复原无 Profile/无默认 Profile状态。字段/API/x86限制已记录；没有个人经典账户，不把发现当49账户导入完成。 证据见 [协议与 Outlook/PST 验收](../../../docs/validation/2026-11-17-protocol-outlook.md)。
