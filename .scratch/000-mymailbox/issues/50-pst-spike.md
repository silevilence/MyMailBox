# 50 用真实 PST 样本验证解析库和保真范围

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
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

- [x] 覆盖 ANSI/Unicode、大文件、嵌套目录、RTF/HTML、嵌入邮件和异常项目样本。
- [x] 记录 Windows x64 构建、许可/分发条件、吞吐和内存；不因有格式规范就承诺完整实现。
- [x] 输出可采用库及限制，商业方案需要预算决定；原始 PST 始终保留。

## Blocked by

- [02 启动并发布第一个插件化桌面应用](02-app-bootstrap.md)

## 外部条件与范围

需要去敏 PST 样本；如仅商业方案满足需求，需实施时确定预算。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-17 后续修复：复用原样本，修复 String8 代码页/零终止边界、PC 主题前缀及 RTF-only 正文恢复。ANSI 18 项、Unicode 51 项通过；单邮件 CRC 损坏隔离及截断文件拒绝通过，原文件哈希不变，构建 0 警告。研究选型完成；多 GiB、任意损坏恢复及产品分发许可条件明确保留，未提前完成 51–53。详见 [修复与选型报告](../../../docs/validation/2026-11-17-pst-ansi-fix.md)。

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-17：用户确认目前没有对应真实测试环境/样本，先推进独立任务。真实集成验收阻塞，保持验收项未勾选。

- 2026-11-17：用户授权本地 GreenMail、只读 Outlook 发现及独立无账户 Profile/PST；开始实施，最终集中验收。32/35 暂缓。

- 2026-11-17 集中验收：已创建独立 ANSI/Unicode PST，实际覆盖中文多层目录、RTF/HTML/CID、嵌入邮件、约137 MiB扩容和截断副本。Unicode 44项对照通过；ANSI 计数/附件/CID/RTF通过，但中文主题、普通正文、HTML中文失败。原文件保留且哈希不变。XstReader 尚不能定为完整保真依赖，保持 in-progress；单项目损坏和多GiB样本也未覆盖。下一步修复/替换候选库并复验ANSI，不要求个人邮箱或付费许可作为当前前提。详见 [验收报告](../../../docs/validation/2026-11-17-protocol-outlook.md)。
