# 92 由 V 或 v Tag 自动发布完整 GitHub Release

Status: needs-info
Progress: not-started
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-24, REQ-03
Blocked by: 91, 87

## Parent

[MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

在推送版本 Tag 后自动核对版本、构建、NSIS 打包、签名/更新清单和上传，再公开 Release。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] V* 与 v* 都触发，非法版本/缺失 changelog/项目版本不一致拒绝；只构建触发 Tag 的提交。
- [ ] Release 正文来自该版本 changelog；安装包、校验和及所选更新清单/签名全部成功后才公开，预发布正确标记。
- [ ] 重跑同一 Tag 可恢复草稿阶段，不移动已发布 Tag 或静默覆盖公开资产；写权限局限发布作业，私钥来自 Secrets。

## Blocked by

- [91 在 GitHub Actions 构建并验证 NSIS 产物](91-ci-build.md)
- [87 验证 NuGet 更新组件与 NSIS 的最小升级](87-updater-spike.md)

## 外部条件与范围

真实触发发布需届时已获发布授权及仓库/CI Secrets；开发可用夹具与不公开的构建验证。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

实施时记录演示步骤或检查命令、结果、适用环境和已知限制；目前未执行，所有验收项保持未勾选。若发现超出预计粒度，先拆为完整可验收子场景；环境不足时保留具体缺口，不标完成。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
