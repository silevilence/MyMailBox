# 53 在重启和插件停用后继续导入

Spec: 000-mymailbox
Status: ready-for-agent
Progress: in-progress
Type: task
Scope: 必做
Estimate: 2–3 人日
Requirements: REQ-06, REQ-07, REQ-04, SUP-01
Blocked by: 49, 51, 08

## Parent

[000-mymailbox · MyMailBox 开发规格](../spec.md) · [需求覆盖矩阵](../coverage.md) · [任务索引](../index.md)

## What to build

统一导入会话支持暂停、恢复、失败项重试与第二导入器插件，避免重复账号/邮件。

本任务应交付可独立演示或验证的完整行为，包含所需数据、服务、UI、插件接入和错误处理；技术路线及通用完成标准见父规格。

## Acceptance criteria

- [ ] 按来源指纹和项目游标续传，源文件变化后重新确认映射。
- [ ] 崩溃、磁盘满和插件停用后已导入内容可读，恢复不会重复创建目录或邮件。
- [ ] 外部测试导入器通过同一向导贡献 schema、进度和损失报告，无宿主分支。

## Blocked by

- [49 预览并导入 Outlook 在线账户配置](49-account-import.md)
- [51 导入 PST 目录和邮件并查看进度](51-pst-import.md)
- [08 在有在途操作时安全更新普通插件](08-plugin-upgrade.md)

## 外部条件与范围

无额外已知外部条件；仍须遵守父规格的资源复用、权限和验收规则。

本项属于必做或必要支撑；模拟/准备工作不能替代要求的真实集成验收。

## 验证与交付记录

本轮实施及验证见下方记录。

## Comments

- 2026-11-16：用户确认任务拆分后创建，尚未开始开发。

- 2026-11-19：文件导入会话持久保存源摘要、导入器版本、参数、目标邮箱、已提交节点和报告。pst-resume 在首封落盘后终止进程，重启继续；利用测试表自增序号耗尽产生 SQLite FULL，核对原文和节点同事务回滚。import-plugin 通过真实可卸载 EML 第二实现的参数、预览、作用域权限、在途取消、重启续传和逐项报告。未使用个人邮箱；49 的真实账户配置导入及其跨会话恢复仍待验。
