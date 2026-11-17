# 16 安全查看保存和打开附件

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-22, REQ-20
Blocked by: 15

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

附件列表支持下载进度、保存、受限预览与外部打开；内联图片和嵌入邮件有正确呈现。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] CID 图片与普通附件区分；保存时防路径穿越，重名策略可见。
- [x] 大文件流式处理可取消，失败不会留下被当作完整文件的临时产物。
- [x] 危险类型不自动执行；预览器可由插件替换，不支持格式提供保存/外部打开。

## Blocked by

- [15 按本封或规则放行远程多媒体](15-remote-content.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：IAttachmentService/IAttachmentPreviewer 提供附件/CID/嵌入邮件选择、受限预览、带进度的流式解码保存及取消。保存名称去路径与非法字符，重名另存；临时文件仅在成功后改为最终名。危险执行/脚本类型仅可保存，其他类型外部打开仍须单独显式操作。预览上限 2 MiB，未知格式提供保存。`dotnet run --project tests/MyMailBox.Checks --no-restore -- attachments` 通过字节核对、重名、危险名称、CID/嵌入邮件、第二预览器及 3 MiB 附件中途取消/清理/来源保留。原邮件仍受 25 MiB 阅读存储上限；输出为流式，无未完成文件冒充成品。窗口验收集中执行。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
