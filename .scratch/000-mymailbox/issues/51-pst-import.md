# 51 导入 PST 目录和邮件并查看进度

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-07
Blocked by: 50, 43, 13

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户选择 PST 和目标本地邮箱后预览目录，开始流式导入并阅读已完成邮件。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 目录层级、原文、附件、收发时间及可支持状态正确映射，非邮件项目明确计数。
- [x] 大文件不整体载入内存，可取消且不会改写原 PST。
- [x] 完成/失败/跳过逐项报告，邮件 ID 稳定，去重不只用 Message-ID。

## Blocked by

- [50 用真实 PST 样本验证解析库和保真范围](50-pst-spike.md)
- [43 创建本地邮箱并按目录保存归档](43-local-archive.md)
- [13 保真阅读 MIME 邮件与原文](13-mime-baseline.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：产品提供只读 PST 目录预览、目标本地邮箱选择、逐封入库、取消及逐项报告。项目以目标邮箱、文件 SHA-256 和源节点去重。pst-import 检查通过 ANSI 目录、原文、时间、已读状态、取消后重入与源文件摘要；独立进程在 137 MiB Unicode 样本读取 36 封邮件、跳过 1 个联系人。MIME 依可用 MAPI 属性重建，单封上限与邮箱一致，为 25 MiB。读取器使用项目内固定源码与 MS-PST 表，产品不包含上游 Crypto.cs。
