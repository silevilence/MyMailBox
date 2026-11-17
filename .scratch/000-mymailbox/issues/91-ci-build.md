# 91 在 GitHub Actions 构建并验证 NSIS 产物

Spec: 000-mymailbox
Status: ready-for-agent
Progress: in-progress
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-24, REQ-02
Blocked by: 86, 90

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

建立 Windows CI，对提交执行构建检查、框架依赖发布与 NSIS 编译，产物留作检查而不自动公开 Release。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] CI 从指定提交构建，固定 SDK/依赖及 Actions 版本，所需检查失败则停止产出。
- [ ] 产物可启动且确认非自包含/无 AOT/无裁剪，记录文件与校验清单。
- [ ] 复用可用缓存，不重复下载测试资源；日志不泄露 Secrets，CI 构建与真实发布分开。

## Blocked by

- [86 使用 NSIS 安装卸载框架依赖应用](86-installer.md)
- [90 校验版本与提取对应 changelog 条目](90-release-metadata.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：已实现 `.github/workflows/build.yml` 与 `scripts/ci-build.ps1`：Windows 2025、精确 SHA checkout、SDK 10.0.301 禁止漂移、NuGet 锁文件、固定 commit 的 Actions、NSIS 3.12 缓存；已有 NSIS 优先复用，确实缺失时才安装固定版本。完整检查和发布启动检查通过后生成安装包、SHA256、逐文件清单和提交元数据，再上传私有于 workflow 的构建产物（不是 Release）。PR 仅有 contents:read，不读取 Secrets。

PowerShell 语法解析通过，Actions tag/SHA 与官方仓库逐一核对；完整本地镜像构建留到本轮统一验收。真实 Actions 尚未执行：本轮调用的 start-tasks 要求只本地提交、不推送，因此不把本地脚本或 YAML 审核当作真实 CI 完成，验收项暂不勾选。

2026-11-17 集中验收补充：完整 scripts/ci-build.ps1 本地镜像通过，包括锁定依赖、所有控制台检查、发布启动、版本/上传夹具和真实 NSIS 打包；真实 GitHub Actions 未触发，Progress 保持 in-progress，不把本地通过冒充远端 CI。 证据见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
