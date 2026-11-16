# 分诊标签

各技能以五个规范分诊角色来表达。本文件把这些角色映射到本仓库问题跟踪器实际使用的标签字符串。

| mattpocock/skills 中的标签 | 本仓库跟踪器中的标签 | 含义 |
| -------------------------- | -------------------- | ---- |
| `needs-triage`             | `needs-triage`       | 维护者需评估该问题 |
| `needs-info`               | `needs-info`         | 等待报告者补充信息 |
| `ready-for-agent`          | `ready-for-agent`    | 已充分明确，可交给 AFK 代理 |
| `ready-for-human`          | `ready-for-human`    | 需由人类实现 |
| `wontfix`                  | `wontfix`            | 不予处理 |

当技能提到某个角色（例如“应用 AFK-ready 分诊标签”）时，使用上表对应的标签字符串。

本仓库使用本地 Markdown 跟踪器（见 `issue-tracker.md`），因此右列的字符串就是问题文件顶部 `Status:` 行所取的值。

如需匹配你实际使用的词汇，直接修改右列。
