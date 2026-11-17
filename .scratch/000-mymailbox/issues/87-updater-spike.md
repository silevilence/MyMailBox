# 87 验证 NuGet 更新组件与 NSIS 的最小升级

Spec: 000-mymailbox
Status: needs-info
Progress: done
Type: research
Scope: 必做验证
Estimate: 2–3 人日
Requirements: REQ-03
Blocked by: 86, 09

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

比较 NetSparkle 与 Octokit 的适配范围，用两个本地测试版本完成检查到安装的可验证演示。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 优先验证现成包，明确 NetSparkle appcast/签名或 Octokit API 的责任边界，选定稳定版本。
- [x] 不引入自包含或替换 NSIS；如不适配，记录原因后采用窄范围 HttpClient 后备。
- [x] 到此任务才检查 Git remote 与项目配置中的仓库地址，缺失时提醒；本地测试源可继续验证非联网环节。

## Blocked by

- [86 使用 NSIS 安装卸载框架依赖应用](86-installer.md)
- [09 展示插件权限和可脱敏导出的诊断](09-plugin-diagnostics.md)

## 外部条件与范围

实施时才需要真实 GitHub owner/repo 和发布测试位置；当前不要求提供。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：已选择并固定 NetSparkleUpdater.SparkleUpdater 3.1.0，比较 Octokit 14.0.0 的边界，补充严格 SemVer 筛选适配（NuGet.Versioning 7.0.3）。真实 remote 确认为 silevilence/MyMailBox；新项目 MyMailBox.Updates 沿用 framework-dependent JIT。

`dotnet run --project tests/MyMailBox.Checks --no-restore -- updater-spike` 通过：真实包加载、Strict 签名 appcast/本地下载与复用、损坏 appcast 和安装器拒绝、退出否决、版本前缀及元数据排序。NuGet 组件本机原无缓存，首次获取后后续验证全部复用。详细选型见 `docs/updater.md`。勾选表示选型实现与定向检查完成；两个本地可执行 NSIS 版本的实际升级统一留到本轮末集中验收，尚未声称安装成功。

2026-11-17 最终集中验收：本项纳入整体 Standards/Spec 审核与完整控制台回归；适用的桌面、PDF、插件或安装场景已在复用本机资源的 Windows 沙盒中验证。具体范围、缺口与日志见 [整体验收记录](../../../docs/validation/2026-11-17-independent-tasks.md)，本项 Progress 保持 done。 实际双版本 0.1.0→0.1.1 NSIS 升级及两种卸载数据策略通过；签名仅使用临时测试密钥，未发布正式版本。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
