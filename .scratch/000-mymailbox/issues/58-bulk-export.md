# 58 批量导出附件和邮件元数据

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-09
Blocked by: 55, 16

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

提供附件筛选/命名模板与 CSV/JSON 元数据导出，展示作用范围和结果清单。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 选择多个账户/目录后导出内容与筛选一致，CSV 处理公式注入，敏感字段选择明确。
- [x] 附件重名、缺失、下载失败和取消逐项报告，保存路径不会越界。
- [x] 外部示例格式插件可通过同一导出向导工作。

## Blocked by

- [55 单封和批量导出原始 EML](55-eml-export.md)
- [16 安全查看保存和打开附件](16-attachments.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：实现账户/目录组合与主题筛选预览、附件筛选/模板、CSV/JSON 显式敏感字段选择和逐项结果。外部 example-exporter 格式通过相同契约注册，包含真实加载/导出/停用/程序集释放测试。修复导出器释放实现引用及示例 JSON 缓存引用；保留原始 Page 六参数构造 ABI。dotnet build 与 dotnet run --project tests/MyMailBox.Checks --no-restore -- bulk-export 通过。覆盖跨账户去重、公式注入、重名/越界、缺失/失败/取消及已完成结果保留。窗口向导验收留到本轮末集中执行。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
