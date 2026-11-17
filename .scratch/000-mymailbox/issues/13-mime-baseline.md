# 13 保真阅读 MIME 邮件与原文

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
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

- [x] 多段正文、嵌套邮件、编码标题和 RFC 日期样本可读，保留原始字节以便导出。
- [x] 头部与正文有明确解析失败回退，不以 Message-ID 为唯一身份。
- [x] 正文加载/解析可取消，恶意深度与大小输入有资源限制。

## Blocked by

- [10 打开一封本地邮件并持久保存到工作空间](10-local-inbox.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：IMimeReader 使用固定 MimeKit 4.17.0 提供编码头部、多段正文、嵌套邮件、Cc、附件/CID 部件树和警告。宿主提供纯文本、MIME 结构及原文预览切换，原文完整保存，预览超过 128 KiB 时明确提示截断。解析限 25 MiB、32 层、2048 部件和单类正文 1 Mi 字符，可取消。`dotnet run --project tests/MyMailBox.Checks --no-restore -- mime mail` 通过全部定向检查。真实窗口验收集中执行。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
