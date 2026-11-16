# 07 在插件管理页安装启用和移除插件包

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-04, REQ-17, SUP-02
Blocked by: 05, 06

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

用户选择插件包后查看来源、版本、依赖和权限，安装到版本目录并启用；缺失依赖不破坏现有功能。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] manifest、SDK 范围、完整性、路径边界和依赖图经过检查，拒绝损坏或不兼容包。
- [x] 安装/启用/停用/移除状态可观察，依赖未满足时保留配置并指出受影响功能。
- [x] 包私有依赖隔离，契约共享；不会覆盖正在使用的 DLL，也不删除其他版本数据。

## Blocked by

- [05 热重载普通插件配置并保留上次有效状态](05-hot-config.md)
- [06 通过普通插件提供并替换页面和选择器](06-ui-contributions.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

插件管理页提供 ZIP 预览/安装/启停/移除；包摘要、SDK、路径、版本目录、引用保护与实际装入测试通过；文件事件和 GUI 共用配置事务。 完整回归与集中 UI 验收已完成，详见 [统一验收记录](../../../docs/validation/framework-plugins.md)。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-16：实现与定向检查完成。插件管理页提供 ZIP 预览/安装/启停/移除；包摘要、SDK、路径、版本目录、引用保护与实际装入测试通过；文件事件和 GUI 共用配置事务。

- 2026-11-16：统一验收完成，见 [验收记录](../../../docs/validation/framework-plugins.md)。
