# 94 验证每一类内置能力都可被外部实现替换

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-04, REQ-05, REQ-06, REQ-07, REQ-09, REQ-10, REQ-11, REQ-12, REQ-13, REQ-14, REQ-15, REQ-16, REQ-17, REQ-18, REQ-20, REQ-21, REQ-22
Blocked by: 47, 53, 58, 64, 79, 88, 08

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

交付 SDK 文档、最小样例与契约测试，对照需求逐项替换至少一个内置实现。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 覆盖账户/认证/收发/存储、导入导出、搜索、规则/调度/条件/动作、阅读编辑/选择器、通知和更新源。
- [x] 样例通过公开契约注册、配置、运行和卸载，无宿主专属类型判断；核心替换在重启后验收。
- [x] 契约有错误/取消/流式/能力/版本规范，明确可信边界与普通插件可卸载条件；不能只以存在接口宣称通过。

## Blocked by

- [47 使用外部邮箱插件验证统一提供者契约](47-provider-sdk.md)
- [53 在重启和插件停用后继续导入](53-import-resume.md)
- [58 批量导出附件和邮件元数据](58-bulk-export.md)
- [64 通过外部插件增加搜索字段和运算符](64-search-plugin.md)
- [79 用外部条件动作和调度插件运行任务](79-automation-sdk.md)
- [88 在设置页和后台检查 GitHub 新版本](88-update-check.md)
- [08 在有在途操作时安全更新普通插件](08-plugin-upgrade.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：公开 SDK 与可安装 ZIP 覆盖邮箱、收发、导入导出、搜索、自动化、编辑器、选择器、通知和更新源。extension-matrix 通过核心 EML 存储跨重启读写、切回 SQLite、标准更新页保存与停用、活动中心实际投递。另复验 mailbox-provider、import-plugin、export、bulk-export、search-extension、task-plugins、drafts、address-book、selectors、activity、update-checks。详见 docs/plugin-sdk.md。
