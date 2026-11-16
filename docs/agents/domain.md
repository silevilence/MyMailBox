# 领域文档

探索代码库时，工程类技能应如何阅读本仓库的领域文档。

## 探索之前先阅读

- 仓库根目录的 **`CONTEXT.md`**，或
- 若根目录存在 **`CONTEXT-MAP.md`**，则读它——它指向每个上下文各自的 `CONTEXT.md`。读取与当前主题相关的每一个。
- **`docs/adr/`** —— 阅读与即将工作的区域相关的 ADR。多上下文仓库中还应查看 `src/<context>/docs/adr/` 里的上下文级决策。

若上述文件不存在，**静默继续**。不要提示其缺失，也不要主动建议先创建它们。`/domain-modeling` 技能（经由 `/grill-with-docs` 和 `/improve-codebase-architecture` 到达）会在术语或决策真正确定时才惰性创建它们。

## 文件结构

单上下文仓库（大多数仓库）：

```
/
├── CONTEXT.md
├── docs/adr/
│   ├── 0001-event-sourced-orders.md
│   └── 0002-postgres-for-write-model.md
└── src/
```

多上下文仓库（根目录存在 `CONTEXT-MAP.md`）：

```
/
├── CONTEXT-MAP.md
├── docs/adr/                          ← 全系统级决策
└── src/
    ├── ordering/
    │   ├── CONTEXT.md
    │   └── docs/adr/                  ← 上下文专属决策
    └── billing/
        ├── CONTEXT.md
        └── docs/adr/
```

## 使用术语表的词汇

当你的产出命名某个领域概念时（问题标题、重构提案、假设、测试名称），使用 `CONTEXT.md` 中定义的术语。不要漂移到术语表明确避开的同义词。

如果你需要的概念尚未进入术语表，那是一个信号——要么你在发明项目不使用的语言（重新考虑），要么存在真实的空缺（记下来，留给 `/domain-modeling`）。

## 标记 ADR 冲突

若你的产出与既有 ADR 矛盾，明确指出，而不是悄悄覆盖：

> _与 ADR-0007（事件溯源订单）矛盾——但值得重新开启，因为……_
