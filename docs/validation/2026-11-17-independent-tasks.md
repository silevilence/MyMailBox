# 2026-11-17 独立任务与整体验收

范围：`.scratch/000-mymailbox/`，包括此前完成的 01–09。用户给予 8 小时时间预算，要求按依赖推进、整体审核验收并更新需求索引。本轮从 2026-11-17 08:05（北京时间）开始；每项实现通过定向检查后本地提交，最后集中执行完整回归、独立审核和真实界面／安装验收。没有推送、正式 Tag 或 Release 操作。

## 交付结论

| 进度 | 工单 | 结论 |
| --- | --- | --- |
| 完成，32 项 | 01–19、43、55–58、69–72、86–88、90 | 实现、定向检查、整体审核及适用的集中验收通过 |
| 进行中，3 项 | 89、91、92 | 实现与本地检查通过；89 的真实 NSIS 升级通过，但完整业务数据场景未齐；91/92 没有真实 Actions/Release 结果 |
| 外部阻塞，7 项 | 20、22、31、32、35、48、50 | 用户确认本轮没有授权测试账户、Outlook.com 应用注册、公司 Exchange 环境、经典 Outlook 或去敏 PST/规则样本 |
| 未开始，66 项 | 其他工单 | 依赖、条件路线或可选范围尚未满足；未绕过前置条件标记完成 |

收尾时重新计算了所有未开始工单的直接依赖：只有尚未选入首版的可选决策 106 满足前置，其余均受未完成上游约束；没有为了耗尽时间预算绕过依赖或默认实施可选路线。

这不是完整邮件客户端的发布验收。在线收发、Outlook/PST 导入、完整搜索、收件规则、写信／发件箱及依赖它们的自动化仍未交付。需求索引已逐行显示开发进度，覆盖矩阵统计关联工单进度，不将这些统计当作整条原始需求已满足。

## 环境与证据

- Windows x64；.NET SDK 10.0.301；x64 Runtime / Windows Desktop Runtime 10.0.12；Avalonia 12.1.1；CordiSharp 0.2.0；FlaUI 5.0.0；NSIS 3.12；WebView2 157.0.4341.46。
- 正式应用与更新助手均为 `-r win-x64 --self-contained false`，禁用 AOT 和裁剪，不包含系统运行时。Windows Desktop Runtime 仅为 FlaUI 工具所需。
- 验证复用本机 SDK、运行时、WebView2、NSIS、NuGet 缓存和本轮构建产物。新增组件仅在本机没有相应版本时获取一次；后续构建、沙盒与下载复用验证均使用已有资源。未重新下载 Release 资产。
- 使用合成 EML、随机临时测试签名密钥与隔离工作空间；没有访问用户真实邮件，没有发信。控制台检查强制设置独立 `MYMAILBOX_HOME`。
- 第一次沙盒的 `vmwp.exe` 在准备测试数据时发生系统访问违例（0xc0000005），环境结束；没有将其计为应用通过。记录见 `artifacts/sandbox-acceptance/vmwp-crash.txt`。恢复后改用沙盒本地磁盘存放 SQLite/WebView 数据并关闭虚拟 GPU；随后在同一沙盒内完成整批验证和必要修复复验。
- 自动化通过 [Windows Sandbox CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli) 执行，UI 用 FlaUI；完成界面测试即关闭测试应用，后续安装核对使用命令行。最终 78 个安装文件摘要、运行时启动和卸载通过，沙盒停止后清单为空、无 MyMailBox 测试进程残留。

最终完整本地 CI 检查的代码提交：`dc5b0d795b9bac283983c293a84c08c8410b4184`。后续提交仅补充验收文档与进度。日志 `artifacts/final-ci-build.log` 包含 33 组控制台检查（含 bootstrap）及完整流水线共 42 条 PASS 记录；构建零警告、零错误。

最终安装产物：`artifacts/installer/MyMailBox-V0.1.0-win-x64-setup.exe`，SHA256 `72842f22c35993bbde0ac0beec5186cbcc8a3274090df24e7dd2b270996c58c3`。提交、SDK、发布方式和逐文件摘要分别保存在同目录 `ci-manifest.json`、`build-manifest.json`、`publish-files.json`、`SHA256SUMS.txt`。这是本地开发验收产物，不是已发布版本。

## Standards

独立审核基线为首个实现之前的 `404652aa35410c225ad83e567fcf9018ec642972`，首次审核终点 `ea92df9`，随后检查修复。依据仓库 AGENTS.md、插件生命周期和发布约束。

1. **P1，已修复：损坏已安装插件影响整体启动。** `PluginPackages.Discover` 未捕获 `InvalidDataException`，损坏 DLL 的校验失败会进入整体启动恢复流程。违反插件失败恢复要求。已隔离该异常；验证坏插件缺席、正常内置插件仍可启动、文件修复后重新发现成功。
2. **P2，已修复：宿主旧页面／服务包装保留外部插件引用。** `PluginScope` 的 Execute 闭包和类型化服务持有外部对象，阻止程序集回收，违反普通插件资源释放约定。已通过可清空目标包装并复制宿主集合。持有旧 Page 的 30 轮卸载、持有 Updates 服务的卸载均通过；旧调用拒绝接单。原生 Avalonia 控件的已知类型保留边界单独说明，不与此缺陷混淆。

本轴 2 项，最高 P1；两项均已修复并验证，没有遗留阻塞发现。

## Spec

独立审核依据工单及规格，区分已实现范围和外部阻塞项。

1. **P2，已修复：HTML-only 邮件导出 TXT 丢失正文。** 57 要求“保留必要头部和正文”。MIME 解析器把提示文案放进 Text 字段，导出器因 Text 非空而不使用 HTML 文本回退。现在 Text 保留正文事实，提示独立进入 Warnings；HTML-only 中文正文可导出，脚本内容被去除。
2. **P2，已修复：多个 CID 资源导出时地址被串改。** 57 要求“HTML 资源规则与安全阅读一致”。全局字符串替换 `cid-1` 会匹配 `cid-10`／`cid-11` 的前缀。现用 DOM 精确匹配 src 并写入 data URI；12 个不同 CID 资源的字节逐项保持一致。

本轴 2 项，最高 P2；两项均已修复并验证。未把尚缺外部环境或未开始的依赖任务当作已完成需求。

## 集中验证中的修复

| 发现 | 修复与复验 |
| --- | --- |
| CordiSharp 根释放时偶发 `ReflectService.Notify` 空引用 | 本地固定版本反编译显示根 Dispose 经过 Restart，通知遍历与 pump 移除子 fiber 存在无锁并发窗口，机制与异常栈吻合；没有转储，不能断言具体哪个引用为空。保存并先释放公开子 PluginHandle，再清理根，插件、配置、贡献、升级和管理组合回归通过；未吞错或盲重试。 |
| HTML 阅读首次创建 NativeControlHost 崩溃 | 增加 Windows supportedOS manifest。与 [Avalonia 上游问题说明](https://github.com/AvaloniaUI/Avalonia/issues/8609) 和 [Windows manifest 文档](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests) 一致。实际 HTML 阅读与 PDF 打印复验通过。 |
| 邮件工具占用过多阅读区域 | 三组工具改为互斥的按需面板并限制高度；无待打开链接时隐藏确认按钮。检查阅读区域占窗口比例，截图确认中文 HTML 正文可见。 |
| 验证工具的旧导出器数量假设及 UIA 时序 | 改为核对唯一 EML 注册；启动时等待最后页面贡献，UIA 查询容忍页面替换期间的短暂 COM 失效；选择下拉项后主动关闭弹层，WebView 截图等待首帧。未修改产品行为来迁就测试。 |

## 需求场景验证

| 范围 | 证据与结果 |
| --- | --- |
| 01–09 原型、插件和权限 | 完整控制台回归；真实模拟原型与 Tab 键路径；安装 v1/v2、在途升级、初始化失败回退；半写入配置恢复、核心启动快照；30 轮外部选择器页面注册／导航／卸载；诊断导出、关闭、重启安全修复。 |
| 10–13、17–18 本地邮件与 MIME | 原文／摘要事务、失败回滚、分页、进程崩溃恢复、完整性、保留缓存与归档；多种编码、错误标记手动覆盖、CID、附件／嵌入邮件、RTF/TNEF 明确降级；真实 EML 导入、分页、四种阅读模式和编码切换。 |
| 11–12 账户与选择 | 创建／改名保持 ID，标签与动态集合、固定范围保存恢复；凭据保护、移除留存与能力过滤的控制台检查；真实账户表单及范围选择通过。 |
| 14–16 安全阅读与附件 | 清理器、CSP、请求拦截及远程规则控制台检查；实际 WebView 中脚本／iframe／CSS／跟踪图片样本的默认远程请求计数为 0；单封放行／撤销反馈和私网拒绝；文本回退、缩放、附件受限预览和保存。 |
| 19、43、55–58 备份归档和导出 | 新建本地邮箱／目录、完整导入、关闭重开；范围预览、JSON 导出、逐项结果；归档冲突重新映射；工作空间恢复到新目录；UI 导出 EML 与源字节相同，HTML/TXT/PDF 成功。 |
| 57 实际 PDF | WebView2 生成 68,610 字节的 5 页 A4 PDF，无 JavaScript；使用已有 Poppler 提取中文头部、100 段正文和末段标记，渲染并检查第 1／5 页，中文、表格及分页可读。 |
| 69–72 提醒与调度 | 虚拟时钟、时区/DST、重复投递防护、错过／重叠／重试／取消、真实进程终止后的领取恢复；UI 即时提醒、延后／取消、CRON 无效提示／10 次预览／保存／暂停／恢复通过。完全退出期间不执行，启动后按策略恢复。 |
| 86 安装／卸载 | 无运行时及仅有 x86 Runtime 资源时退出 1638；复用 x64 Runtime 后在中文含空格路径安装成功。默认静默卸载保留默认／自定义工作空间及安装目录未知文件；交互删除默认不勾选，勾选后二次确认，仅删除默认工作空间。 |
| 87–89 更新 | 固定真实 NetSparkle 包、签名清单／安装器、哈希、损坏／取消／重启复验、替换源和插件停止；真实公开仓库只读检查未下载资产；沙盒内临时测试密钥签名的真实 0.1.0→0.1.1 NSIS 经桌面安装动作完成保存／排空／备份／退出／助手验签安装／新版本启动确认，原文与插件文件保留。 |
| 90–92 发布基础 | 严格 SemVer、V/v 重复、精确提交、有效版本及 changelog 分类和正文提取；真实签名工具；模拟 transport 验证草稿先行、资产完整／摘要核对、失败续传、公开版本重跑只读。完整本地 CI 镜像通过，真实 Actions/Release 未执行。 |

主要本地证据：

- `artifacts/final-ci-build.log`：最终完整构建与非 UI 检查。
- `artifacts/sandbox-acceptance/acceptance/ui-20261117-110840/result.txt`、`features-result.txt` 及 `ui.log`：完整集中 UI；同目录截图 10–14 为新功能。
- `artifacts/sandbox-acceptance/reading-frame/reading-result.txt`、`10-html.png`、`native-windows.txt`：最终阅读修复复验；等待 WebView 首帧后确认实际正文，原生窗口尺寸／可见性记录保留。
- `artifacts/sandbox-acceptance/pdf-check/`：实际 PDF、提取文本与首尾页 PNG。
- `artifacts/sandbox-acceptance/acceptance/upgrade/upgrade-result.txt`：真实升级完成状态、独立备份路径及数据保留检查。
- `artifacts/sandbox-acceptance/uninstall-delete.log`、`complete.txt`、`final-installer.json`：卸载选择、完整沙盒批次及最终安装文件摘要核对。

失败轮次的日志和截图保留用于追溯；历史 `failure.txt` 不是最终结论，以上明确的 PASS 结果及最终产物核对为准。验收资源位于忽略的 artifacts 目录，不把测试签名密钥、个人数据或生成文件提交为源码。

## 复现与剩余边界

```powershell
# 完整非 UI 检查和发布产物
./scripts/verify.ps1
# 干净受跟踪工作区的本地 CI 镜像
./scripts/ci-build.ps1 -ExpectedCommit (git rev-parse HEAD)
# 最终阶段集中执行界面（使用新建的隔离工作空间）
dotnet run --project tests/MyMailBox.UiChecks --no-build
# 只回归阅读布局、HTML、附件及四种导出
dotnet run --project tests/MyMailBox.UiChecks --no-build -- reading
```

`MYMAILBOX_UI_EXE` 可指向已安装测试程序，`MYMAILBOX_UI_OUTPUT` 可指定本地证据目录。沙盒中的活动数据库和 WebView 用户数据放在沙盒本地磁盘，结束后复制证据；已有运行时／WebView2 目录只读共享，不下载重装。

高级自定义 Avalonia 原生动画控件仍有已知类型保留，真实停用会明确提示需重启；普通宿主页面／选择器的卸载通过。这是已明确的高级扩展边界，不承诺任意原生控件可热卸载。

正式公钥仍为空，GitHub Actions Secret 尚未配置；更新 UI 明确只能检查。89 的真实草稿／规则／待确认发送状态依赖未完成上游任务，因此不宣称完整更新业务验收。91–92 的正式远端验证需日后明确授权提交推送／发布，并按 generate-changelog 流程执行；本轮没有要求用户重复提供已有授权，也没有擅自发布。99–108 可选扩展未自动纳入实现。
