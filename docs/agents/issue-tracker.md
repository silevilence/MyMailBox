# 问题跟踪器：本地 Markdown

本仓库的问题与规格（可能被称为 PRD）以 Markdown 文件保存在 `.scratch/` 下。

## 约定

- Spec 指整个需求目录，每份一个目录：`.scratch/<三位编号>-<英文代号>/`，当前为 `.scratch/000-mymailbox/`。英文代号使用小写 kebab-case。
- 规格统一登记在 `.scratch/index.md`，按创建登记顺序分配仓库内唯一编号 `000`、`001`、`002`……（数字至少三位）；编号不表示优先级或发布版本。已分配编号不得重排或复用，撤销的规格也保留登记。
- 修订现有规格保留目录编号与代号；独立的新需求规格创建新目录并分配新编号。登记总索引时同时记录目录入口和创建日期。
- 目录内规格文件统一为 `spec.md`，不在文件名中重复编号和代号。标题包含目录名和中文名称，正文包含 `Spec:`（目录名）、`Codename:`、`Created:` 元数据。
- 实现类问题每个工单一文件，位于 `.scratch/<三位编号>-<英文代号>/issues/<NN>-<slug>.md`，从 `01` 开始编号——绝不合并成单个 tickets 文件
- 工单顶部用 `Spec: 000-mymailbox` 这样的目录名标明所属规格，`Parent` 链接显示目录名。规格内可沿用任务号 `01` 和需求号 `REQ-01`；跨规格引用使用 `000-mymailbox/01`、`000-mymailbox/REQ-01` 并附文件链接，避免重号歧义。
- 分诊状态记录在每个问题文件顶部的 `Status:` 行（角色字符串见 `triage-labels.md`）
- 评论与讨论历史追加到文件底部的 `## Comments` 标题之下

## 当技能要求“发布到问题跟踪器”

在 `.scratch/<三位编号>-<英文代号>/` 下新建文件（必要时登记并创建目录）。

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
