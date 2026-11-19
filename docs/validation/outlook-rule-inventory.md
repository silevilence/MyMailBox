# 经典 Outlook 规则发现

2026 年 11 月 19 日。本机保留的 Outlook 测试记录显示独立 Profile 中有 0 个账户，PST 样本用于邮件保真对照。54 的本地验证覆盖规则清单结构及保守判定。真实 Profile 的提供者可见性与两端匹配语义仍待验收。

| Outlook 源字段 | 清单保留的内容 | 进入 68 的条件 |
| --- | --- | --- |
| Rule | 名称、启用状态、执行顺序、收件或发件方向、仅客户端标志 | 核对账户与触发阶段，只评估收件规则 |
| 标题、正文、标题或正文、收发地址文字 | 条件及例外分开，保留全部词值及类型编号 | 在相同样本上核对匹配方式、多值组合及例外 |
| 标记已读、停止处理、分类 | 动作类型、顺序、分类值 | 在 MyMailBox 预览结果中核对顺序与停止语义 |
| 移动、复制 | 可读取的目标路径、EntryID 与 StoreID，读取失败保留原因 | 由用户映射到有效目标。复制动作需要相应实现 |
| 未知条件、脚本、外部程序、删除或自动外发 | 原规则名、类型编号、动作位置与不可转换原因 | 整条保留供手工核对，保持禁用 |
| 日期与大小区间 | 可枚举类型，但 OOM 不公开区间值 | 在原客户端核对边界后手工建立 |

微软的 Rule 对象同时表示服务器及客户端规则。OOM 只覆盖规则向导的一部分，未知项的原文脚本或区间参数可能无法读取。清单中的候选项仍需与实际样本对照。[Rule 对象](https://learn.microsoft.com/en-us/office/vba/api/outlook.rule)、[条件枚举及区间限制](https://learn.microsoft.com/en-us/office/vba/api/outlook.olruleconditiontype)、[动作枚举](https://learn.microsoft.com/en-us/office/vba/api/outlook.olruleactiontype)

在「收件规则 → 从经典 Outlook 规则清单转换」读取 JSON，选中源规则，再选本机账户或目录。若动作涉及移动，须另外选择目标目录。页面逐项显示条件、例外和动作的映射。点击「对照本机邮件并载入停用草稿」可查看实际样本命中情况。保存后的规则默认停用，可在核对范围及顺序后手动启用。

目前转换主题、正文及两者择一的文本条件，同一字段的多个词按 OR 组合。标为已读、分类、单个同账户移动和停止后续处理可保留。地址字符串、复制、脚本及缺失目标等项目会让整条规则留在清单中供手动核对。源 Profile 的实际命中情况仍需在对应 Outlook 环境中验证。[微软的多值文本规则说明](https://learn.microsoft.com/en-us/office/vba/api/outlook.textrulecondition.text)

在仓库根目录运行 `./scripts/inspect-outlook-rules.ps1 -SelfTest` 可离线复验条件、例外、顺序、停止标志，以及失效目录、脚本、未知类型的留存。实际采集时由用户打开授权的经典 Outlook 测试 Profile，指定 `-Profile`、`-StoreName` 和 `-Output`（新的本地 JSON 文件）。脚本仅附着到当前会话并核对 Profile 和存储名称。清单可能包含账户、目录和规则原文，请保存在 Git 忽略的本地验收目录。
