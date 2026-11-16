# 问题跟踪器：本地 Markdown

本仓库的问题与规格（可能被称为 PRD）以 Markdown 文件保存在 `.scratch/` 下。

## 约定

- 每个特性一个目录：`.scratch/<feature-slug>/`
- 规格文件为 `.scratch/<feature-slug>/spec.md`
- 实现类问题每个工单一文件，位于 `.scratch/<feature-slug>/issues/<NN>-<slug>.md`，从 `01` 开始编号——绝不合并成单个 tickets 文件
- 分诊状态记录在每个问题文件顶部的 `Status:` 行（角色字符串见 `triage-labels.md`）
- 评论与讨论历史追加到文件底部的 `## Comments` 标题之下

## 当技能要求“发布到问题跟踪器”

在 `.scratch/<feature-slug>/` 下新建文件（必要时创建目录）。

## 当技能要求“获取相关工单”

读取所引用路径的文件。用户通常会直接传入路径或问题编号。

## 寻路（Wayfinding）操作

由 `/wayfinder` 使用。**地图（map）** 是一个文件，每个工单对应一个 **子（child）** 文件。

- **地图**：`.scratch/<effort>/map.md` —— 承载 Notes / Decisions-so-far / Fog 正文。
- **子工单**：`.scratch/<effort>/issues/NN-<slug>.md`，从 `01` 开始编号，正文中写明问题。`Type:` 行记录工单类型（`research`/`prototype`/`grilling`/`task`）；`Status:` 行记录 `claimed`/`resolved`。
- **阻塞关系**：文件顶部附近的 `Blocked by: NN, NN` 行。当它列出的所有文件均为 `resolved` 时，该工单即解除阻塞。
- **前沿（Frontier）**：扫描 `.scratch/<effort>/issues/`，找出未关闭、未被阻塞且未被认领的文件；编号最小者优先。
- **认领**：在做任何工作之前设置 `Status: claimed` 并保存。
- **解决**：在 `## Answer` 标题下追加答案，设置 `Status: resolved`，然后把上下文指针（要点 + 链接）追加到 `map.md` 的 Decisions-so-far 中。
