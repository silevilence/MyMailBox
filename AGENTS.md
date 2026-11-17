## Agent skills

### Issue tracker

问题与规格以 Markdown 文件形式保存在本仓库的 `.scratch/<feature>/` 下，不使用外部跟踪器。详见 `docs/agents/issue-tracker.md`。

规格总索引为 `.scratch/index.md`。Spec 指整个需求目录，目录按 `<三位编号>-<英文代号>` 命名，从 `000` 起递增；当前为 `.scratch/000-mymailbox/`。目录内规格文件统一叫 `spec.md`，不在文件名中重复编号。新增规格先登记，已有编号不重排、不复用；工单通过目录名标明所属规格。

当前 MyMailBox 开发任务入口为 `.scratch/000-mymailbox/index.md`，规格见 `.scratch/000-mymailbox/spec.md`，需求覆盖见 `.scratch/000-mymailbox/coverage.md`。每项工单在 `issues/` 中独立保存；开始实现前检查直接依赖、条件路线和外部环境，不将分诊就绪视为开发完成。

### Triage labels

五个分诊角色使用与其同名的标签字符串（`needs-triage`、`needs-info`、`ready-for-agent`、`ready-for-human`、`wontfix`）。详见 `docs/agents/triage-labels.md`。

### Domain docs

单上下文：仓库根目录的 `CONTEXT.md` 与 `docs/adr/`。详见 `docs/agents/domain.md`。

## 编码、验证与 UI/UX 设计

- 所有实际 UI 测试统一安排在当次任务目标整体完成后进行；一次执行多个任务时，须等所有任务完成后再集中测试。只有存在确实必要性时才可提前进行，单纯的防御性测试不构成例外。优先使用 UI 自动化脚本，而非 Computer Use。
- 编码与验证时，禁止过度思考和过度的防御性编程；禁止因过度考虑极端情况的处理而拖慢正常功能的实现进度。根据实际需求与已知风险确定实现和验证范围。
- 所有 UI 与 UX 工作都必须使用适当的技能进行恰当的设计，并从用户实际操作的角度审视界面、交互流程与反馈。

## 已确定的技术路线

### 当前实现与验证入口

- `MyMailBox.Contracts` 为共享契约；`Runtime` 管理 CordiSharp 适配、配置及插件包；`Mail` 实现本地 SQLite/MIME、导出备份与调度；`Updates`、`UpdateHost` 分别负责更新校验和进程退出后的安装；`Builtins` 注册能力，`Desktop` 提供宿主渲染 UI。
- `scripts/verify.ps1` 执行完整非 UI 检查；`tests/MyMailBox.Checks` 支持组名定向执行并强制隔离数据。完整 UI 由 `tests/MyMailBox.UiChecks` 在最终阶段集中运行，保留输出与适用范围。
- Windows 原生 HTML 宿主依赖 `Desktop/app.manifest` 的 supportedOS 声明。插件服务包装停用时须清空外部调用目标；停止运行时先释放保存的 CordiSharp 子 PluginHandle，再释放根。
- 真实邮件协议、Outlook/PST、完整搜索规则和自动化任务仍以需求索引的未完成项为准；不把本地模拟或发布工作流文件等同于真实集成验收。

- Windows x64，Avalonia、C#、.NET 10。
- 全 JIT、框架依赖发布（framework-dependent），不采用 NativeAOT，不使用自包含发布。系统 .NET 运行时由用户安装；安装器检查所需版本和架构并提供安装指引，不捆绑运行时。
- 发布参数须明确包含 `-r win-x64 --self-contained false`，禁用 `PublishAot` 和 `PublishTrimmed`，避免影响动态插件。
- 插件系统优先复用 CordiSharp，通过项目适配层补充生命周期、配置验证、版本兼容和失败恢复；不要未经评估另写完整插件框架。
- 核心/普通插件与内置/外部插件是不同维度。核心插件配置仅启动加载，普通插件配置热重载；普通插件须遵守依赖、在途操作和资源释放约定。
- 完整需求与技术背景见 `docs/需求与技术可行性评估.md`；其中 AOT 对比为已评估的备选背景，不是待实施路线。

## 版本与发布流程

正式发布统一使用：**generate-changelog 技能生成变更记录并同步版本 → 提交发布准备变更 → 打版本 Tag → 推送该提交与 Tag → GitHub Actions 自动构建、打包并发布 GitHub Release**。

### 发布前准备

- 真正准备指定版本时使用 `generate-changelog` 技能。仅讨论或阅读发布规范时，不执行生成流程。
- 版本输入接受 `V0.1.0` 或 `v0.1.0`；版本前缀忽略大小写，展示与新建 Tag 统一使用 `V<version>`，项目版本字段去掉前缀。Git Tag 本身区分大小写，因此需检测已有 `V`/`v` 形式，不能重复发布同一个规范化版本。
- 依据最近一个 `V`/`v` 发布 Tag 之后的 Git 历史和实际变更生成面向用户的记录。首次发布没有历史 Tag 时，仅列“✨ 新功能”，不将开发期间修复写成已发布版本的 Bug 修复。
- 根目录 `changelog.md` 使用 `# Change Log`，版本条目采用 `## V<version>`，新版本置顶；按需使用“✨ 新功能”“🚀 优化”“🐛 Bug 修复”，省略空分类。
- 检查根目录和子项目，更新所有本次发布耦合的应用、插件及分发版本声明；不修改第三方依赖版本、历史 changelog 或生成文件来凑版本一致。
- Tag 必须指向已包含本次 changelog 与版本声明的提交。发布前完成相应检查；只提交本次范围内的变更。
- 说明发布流程或要求生成 changelog 本身不构成提交、打 Tag、推送或发布授权；用户明确要求发布、打 Tag 或推送时，按已授权范围执行，不重复索取已有授权。

### GitHub Actions 发布契约

- 发布由版本 Tag 的 push 事件触发，Tag 过滤同时覆盖 `V*` 与 `v*`；工作流内部再次校验合法版本，不能仅凭前缀发布。
- 从触发 Tag 的精确提交构建，核对 Tag 版本、项目版本与 `changelog.md` 对应条目一致；不在 CI 临时生成未提交的用户变更记录或默默修正版本不一致。
- 使用 Windows 构建环境，完成必要检查后进行 win-x64 框架依赖发布，再用 NSIS 打包。
- Release 正文提取对应版本的 changelog 条目，不用原始提交列表或 GitHub 自动生成文本替代；保留版本分类和格式。
- 上传 NSIS 安装包、校验和及更新组件实际需要的清单/签名；若选 NetSparkle，同步生成 appcast 及其签名。签名私钥通过 CI Secrets 提供，不写入仓库。
- 所有校验及资产上传成功后再公开 Release，避免更新客户端看到未完成的发布。预发布版本标记 prerelease，稳定更新通道不选中预发布。
- 创建 Release 的作业使用所需的 `contents: write` 权限；版本化第三方 Actions，并按确定的依赖策略固定版本。
- 失败重跑应继续处理同一 Tag，不能移动或重新创建已发布 Tag，也不能未经判断覆盖已公开的版本资产。
- 发布后优先核对平台摘要、资产名称/大小和 CI 上传记录，复用本地产物；禁止仅为核对哈希将所有 Release 文件重新下载。

## 自动更新

- 更新源使用本项目 GitHub Releases，更新检查与 UI 作为内置插件能力提供；保留可替换的更新源接口，安装执行与检查分层。
- 优先验证现成 NuGet 包：`NetSparkleUpdater.SparkleUpdater` 用于检查、下载、签名验证及调用 NSIS 安装包；其 appcast 必须由 Actions 生成并随 GitHub Release 分发，不能误认为它直接解析普通 Release API。UI 可使用自定义实现，Avalonia UI 包仅作可选项。
- 若选择只读取 Releases 的轻量检查方案，评估 `Octokit`；它是 GitHub API SDK，不是完整更新安装框架。若现成方案不适合项目范围，直接使用 `HttpClient` 对接 GitHub Releases API 实现小范围检查，无需为此搭建更新服务端。
- 不因更新组件而改用自包含发布或替换已选 NSIS 安装机制。包版本在实现时核实、固定并验证，不把上游开发分支功能当作已发布稳定能力。
- 检查须正确处理 `V`/`v` 前缀、语义版本、预发布通道、draft、win-x64 资产、缺少资产、超时/代理/限流与缓存；失败要区分“无新版本”和“无法检查”。
- 下载后校验，保存草稿并排空关键任务后才执行安装；保留用户邮件、配置和插件数据。更新失败与数据库迁移失败需可诊断、可恢复。
- **实施自动更新功能时**，先从仓库远程配置和项目配置确认 GitHub owner/repo。若届时仍无有效地址，再提醒用户提供；目前不要求地址，也不编造或硬编码占位仓库。公开 Release 检查不在客户端嵌入私人访问 token。
- 候选依据：[NetSparkle](https://github.com/NetSparkleUpdater/NetSparkle)、[Octokit](https://github.com/octokit/octokit.net)。
