# 86 使用 NSIS 安装卸载框架依赖应用

Spec: 000-mymailbox
Status: ready-for-agent
Progress: done
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-02, REQ-01
Blocked by: 02, 19

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

制作 win-x64 NSIS 安装包，完成运行时检测、安装、启动、升级入口和卸载数据选择。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [x] 正确区分 .NET 10 x64 Runtime、x86 和 SDK；缺失时引导用户安装，不捆绑或擅自下载运行时。
- [x] 按需检测 WebView2，程序/数据/插件目录分离；安装路径、快捷方式和卸载信息正确。
- [x] 卸载默认保留邮件及归档，删除数据需明确选择；无 .NET 和有 .NET 场景用已备资源验收。

## Blocked by

- [02 启动并发布第一个插件化桌面应用](02-app-bootstrap.md)
- [19 备份并恢复完整工作空间](19-backup.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

2026-11-17：NSIS 实现 x64 运行时实测检测、缺失安装指引、中文自定义路径、用户快捷方式/卸载信息、WebView2 检查、程序/工作空间分离与默认保留数据卸载。发布产物和本机 NSIS 复用，scripts/publish.ps1 与 scripts/package-installer.ps1 通过，生成安装包、SHA256SUMS 和逐文件清单。以上勾选表示实现及编译检查通过；无/有运行时、安装/升级/卸载的实际行为将在本轮末集中验收，尚未声称通过。准备清单见 docs/release.md。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
