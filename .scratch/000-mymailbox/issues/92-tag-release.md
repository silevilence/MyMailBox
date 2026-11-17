# 92 由 V 或 v Tag 自动发布完整 GitHub Release

Spec: 000-mymailbox
Status: needs-info
Progress: in-progress
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-24, REQ-03
Blocked by: 91, 87

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

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

2026-11-17：已实现 V*/v* Tag workflow，复用 91 的精确 SHA Windows 构建作业，发布作业单独获得 contents:write；再次校验 Tag/changelog 后签名安装器与 appcast，创建/恢复草稿、按平台 digest/size 校验全部 7 项资产，最后才公开。已公开 Release 仅允许只读比对，差异明确失败；草稿重跑复用一致资产并修复未完成上传，不移动 Tag。

`release-signing` 定向组与 `scripts/test-publish-release.ps1` 通过：真实 Ed25519 包与 CLI、客户端公钥匹配、V/v 下载地址、完整摘要；模拟 API 验证上传失败保持草稿、重跑复用、公开后禁止覆盖和精确提交。所有用例仅操作本地夹具，没有创建远程 Release、推送或 Tag。正式发布仍需用户授权、已提交的 changelog/版本、正式公钥与 CI Secret，且 91 真实 Actions 尚未验收，因此本项保持 in-progress、验收框未勾选。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
