# 13 保真阅读 MIME 邮件与原文

Spec: 000-mymailbox
Status: ready-for-agent
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-22, REQ-07
Blocked by: 10

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

使用 MIME 解析插件呈现主题、地址、日期、正文及附件树，并支持原文查看。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 多段正文、嵌套邮件、编码标题和 RFC 日期样本可读，保留原始字节以便导出。
- [ ] 头部与正文有明确解析失败回退，不以 Message-ID 为唯一身份。
- [ ] 正文加载/解析可取消，恶意深度与大小输入有资源限制。

## Blocked by

- [10 打开一封本地邮件并持久保存到工作空间](10-local-inbox.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
