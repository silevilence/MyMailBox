# 79 用外部条件动作和调度插件运行任务

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-11, REQ-12, REQ-13, REQ-14, REQ-15, REQ-04
Blocked by: 64, 78, 08

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

分别以第二实现贡献条件、动作和调度方式，在同一向导配置并执行完整任务。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] SDK 包含参数 schema、预览、执行、取消、错误分类、能力及版本声明。
- [x] 插件卸载时依赖规则/任务暂停并保留配置，不能跳过未知条件继续执行。
- [x] 替换插件版本后可迁移参数，回滚/失败有说明，定时器和回调不泄漏。

## Blocked by

- [64 通过外部插件增加搜索字段和运算符](64-search-plugin.md)
- [78 审查自动化历史并从失败步骤继续](78-task-history.md)
- [08 在有在途操作时安全更新普通插件](08-plugin-upgrade.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-18：task-plugins 实际加载可卸载外部插件的条件、动作、调度三类贡献，UTC 触发后宿主写入邮件分类。验证参数 schema、影响预览、账户授权、取消、停用暂停、重载旧快照、v1→v2 参数迁移及回滚保护。两版示例加入 SDK 样例包入口。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
