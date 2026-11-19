# 103 验证并提供 S/MIME 签名与加密阅读发送

Spec: 000-mymailbox
Status: needs-info
Progress: not-started
Type: task
Scope: 可选扩展，需进一步细化后实施
Estimate: 2–3 人日
Requirements: OPT-02
Blocked by: 16, 26, 09

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

在确认需要后增加 S/MIME 能力，配置证书并完成签名/验证/加解密样例。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 签名有效、过期、不受信任和正文篡改有清晰状态，缺私钥不假装成功。
- [ ] 私钥安全保存，正文和索引泄露范围明确。
- [ ] 先界定证书来源和支持算法，若完整实现超过一个迭代则按读/写再拆；不影响普通 MIME。

## Blocked by

- [16 安全查看保存和打开附件](16-attachments.md)
- [26 回复全部和转发时保留上下文与身份](26-reply-forward.md)
- [09 展示插件权限和可脱敏导出的诊断](09-plugin-diagnostics.md)

## 外部条件与范围

需确认需求、证书来源及授权样本。

本项尚未选入首版，不能自行扩大必做范围。决策型任务完成后应生成细化实现任务，再实施所选能力。

## 验证与交付记录

2026-11-19 本地评估已完成。已验证 CMS 签名、信任、到期、篡改及加解密失败情形。 此可选范围尚未选入首版。证据、限制与后续验收见[可选项本地评估](../../../docs/validation/2026-11-19-task-000-optional.md)。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。
