# 2026-11-17 本地协议、Outlook 与 PST 验收

后续更新：本报告保留该批次历史结论。50 的 ANSI 问题现已修复并复验，最新状态见 [PST 修复与选型报告](2026-11-17-pst-ansi-fix.md)；32/35 也已获授权恢复推进。

## 结论

| 工单 | 结论 | 边界 |
| --- | --- | --- |
| 20 IMAP | 完成 | GreenMail 实际 TLS 认证、中文目录、只读分页；尚不包含 21 的增量同步/离线正文 |
| 22 SMTP | 完成 | 纯文本 To/Cc/Bcc，独立认证，服务器拒绝/部分接收/不明结果；无自动重发，尚不包含持久发件箱 |
| 31 POP3 | 完成 | 原文及 UIDL 同事务、重启去重、来源保留/删除、无 UIDL 限制模式 |
| 48 Outlook 发现 | 完成调查 | 经典 x64 与新 Outlook 区分，实际独立 OOM 会话；没有个人经典账户样本，未宣称完成账户导入或 x86 实测 |
| 50 PST | 进行中 | 独立样本、解析对照和性能记录完成；候选库 ANSI 中文/普通正文不保真，不能作为完整导入依赖定案 |

32、35 按用户要求暂缓，没有连接个人 Outlook.com 或公司 Exchange。未推送、打 Tag 或发布。整体项目进度为 36 完成、4 进行中、2 暂缓/blocked、66 未开始。

## 需求与代码审核

- 收发配置通过共享 IProtocolMailService 与宿主渲染页面接入现有账户插件；运行时包装在途操作、取消和释放。保存配置不联网，收/发密码分别以 Windows DPAPI 保护；可移植备份清空 credentials，移除账户同时清理两类秘密。
- IMAP 使用 MailKit 4.17.0，目录用途来自服务属性，摘要不读取正文，文件夹以只读方式打开；中文目录和连续页 UID 无重叠通过。HTTP CONNECT/SOCKS5 实际隧道接入通过。
- TLS 不自动降级、不全局忽略证书。测试指纹仅在独立端点配置，仍检查主机名及证书有效期，不安装根证书。GreenMail IMAP/POP3 不提供 STARTTLS/STLS，本轮验证其明确拒绝且不降级；成功 STARTTLS 会话未在本轮服务上验收。OAuth2 传输可配置，但真实提供者 OAuth 授权/刷新仍属于对应提供者任务。
- SMTP 发件地址来自当前可发送账户；服务器检查许可。Bcc 只进入 SMTP 信封，收件原文无 Bcc。拒绝全部、部分 RCPT 接收、MAIL FROM 拒绝、DATA 后断线均有明确结果；已接收或不明结果的界面禁止再次点击发送，需新建下一封。
- POP3 来源键包含账户与端点，原文和 UIDL 同一 SQLite 事务提交，校验本地原文后才 DELE，QUIT 才提交远端删除。中途断线重新进入不会重复导入；存储写入失败、本地副本损坏均保留来源。无 UIDL 仅可按原文摘要去重并保留来源，明确说明不能区分内容完全相同的独立邮件。
- 桌面反馈和取消固定在可见区域，字段有标签，操作期间禁用冲突操作；按账户能力禁用 IMAP/发送/POP 页，POP 不伪造远端目录。

审核发现并已修复：连接/认证增加明确的 15 秒超时；成功认证清除陈旧失败状态；长表单的结果反馈移出滚动区；测试 TLS 参数、故障服务停止竞态以及 Outlook 默认草稿箱行为修正后复验通过。

## 自动化与实际桌面证据

- `scripts/verify.ps1`：完整控制台回归与 framework-dependent 发布启动检查通过。主解决方案构建 0 警告、0 错误。
- `protocols`：配置、能力、凭据隔离/保留/清理与非法地址检查通过。
- `protocol-live`：实际 GreenMail TLS SMTP→IMAP/POP3、中文目录、分页、未读状态、证书错误、密码错误与恢复、HTTP/SOCKS5 代理、POP 重启去重和服务器副本删除通过。
- `protocol-faults`：实际本机 TLS 故障服务验证部分拒绝、所有收件人拒绝、发件身份拒绝、DATA 确认丢失、POP 中断恢复、无 UIDL、写入故障及本地损坏来源保护通过。
- FlaUI 协议批次：保存/重新打开配置、字段错误、IMAP 目录分页、认证失败恢复、To/Cc/Bcc 发送、重复发送保护、POP 收取/去重及本地落盘、关闭进程通过。最终布局证据：`artifacts/protocol-acceptance/ui-20261117-123706/`。
- 完整既有 FlaUI 回归通过：原型/键盘、本地阅读与四种导出、账户与选择器、归档备份、提醒/CRON/更新设置、插件安装升级/回滚、30 次导航卸载、诊断与安全启动修复。证据：`artifacts/protocol-acceptance/full-ui.log` 与 `full-ui/`。
- 复用本机 Visual Studio 的 Microsoft.CodeCoverage.Console，未下载覆盖率工具。最终 `coverage-final.xml` 按服务源文件去重可执行行：ProtocolMailService 118/120（98.33%），ProtocolPop 74/76（97.37%），ProtocolSmtp 36/36（100%），合计 228/232（98.28%）。这是服务代码行覆盖率，不能冒充 UI、外部库或所有分支覆盖率。未覆盖行为主要为连接超时例外和去重记录所指邮件已被外部删除的恢复分支。

日志：`artifacts/protocol-acceptance/full-verify-final.log`、`coverage-final.log`、`protocol-live.log`、`protocol-faults.log`、`ui-final.log`。早期失败日志保留追溯，以明确最终 PASS 和本报告为准。

收尾核验：`cleanup.json` 记录 GreenMail 已停止，Outlook 和桌面测试进程均为 0；最终账户/协议定向回归及发布构建通过，见 `account-final.log`、`publish-final.log`。

## 规范核对及未解决发现

| 核对项 | 结果 |
| --- | --- |
| Windows x64、.NET 10、JIT、framework-dependent | 通过；没有更改为自包含/AOT/裁剪 |
| 现有插件契约与普通插件在途/释放 | 通过；新服务有 scope 包装，宿主渲染界面 |
| 凭据、错误与取消 | 通过；DPAPI、服务器异常不直接显示、取消入口和连接超时 |
| 资源复用与最终集中 UI | 通过；既有 Java/依赖/工具复用，修复后只回归受影响部分 |
| 服务代码行覆盖率 ≥90% | 98.28%；未把分支覆盖率或第三方库覆盖率计入该数字 |
| 全部五项可否标完成 | 20/22/31/48 可以；50 不可以 |

🟠 **Important：候选 PST 库的 ANSI 保真缺口。** 见 [固定上游 LTP 字符串解析](https://github.com/Dijji/XstReader/blob/3b7856c8b3d01bdf8aa13744e476d1ef7761b832/XstReader.Base/LTP.cs) 和本地 `final/comparison.json`：当前源码以 UTF-8 处理部分 ANSI 字符串，真实中文主题/HTML 已损坏，普通正文也缺失。不能先启用导入再静默忽略；需验证代码页/长度读取修复或替代解析路径后再推进 51。其余本轮产品实现未发现未解决的阻塞问题。

## Outlook 验证及恢复

只读安装发现确认经典 Outlook **16.0.20527.20196 x64**，新 Outlook **1.2026.1103.200**。初始无经典 Profile、无默认 Profile、无运行进程。

通过 `/PIM MyMailBoxAcceptance…` 创建独立无账户 Profile，实际核对 CurrentProfileName 和 Accounts.Count=0 后才进行样本写入。OOM 会话版本 `16.0.0.20527`；枚举与数据文件操作均在该测试会话内。未启动个人 Profile，不读取密码/token，不发送邮件，不导入任何个人邮件。

结束后释放 COM、正常退出测试 Outlook，清理本轮创建的 Profile 及首次启动生成的无账户 scaffold；恢复原默认 Profile 的存在性/值。默认草稿存储也移入 artifacts 保留，不留在用户文档目录。`inventory-cleaned.json` 确认经典 Profile 列表为空、默认 Profile=null、无 Outlook 进程，与测试前一致。新 Outlook 安装和登录缓存未操作。

OOM/Account Management API/MAPI 字段及 x86 边界见 [兼容调查](outlook-compatibility.md)。本轮不能证明真实个人经典账户完整字段读取；49 的实际导入仍需对应样本和重新授权。

## PST 对照结果

样本位于 `artifacts/outlook-acceptance/final/`，完全由测试内容生成。初轮 Outlook 把 MailItem 保存到默认 Drafts，修正为显式 Move 到目标 PST，并增加项目数断言后重制；初轮文件保留，不作为合格样本。

| 样本 | 大小与内容 | 固定 XstReader 源码结果 |
| --- | --- | --- |
| Unicode | 143,754,240 字节；36 封邮件、1 个联系人、32 个 4 MiB 附件，另有中文/HTML/CID/RTF/嵌入邮件 | 37 项、35 个附件（含嵌入邮件附件）、1 封嵌入邮件；44 项对照通过 |
| ANSI | 271,360 字节；4 封典型邮件 | 计数、附件字节、CID、RTF 通过；中文主题、普通正文、HTML 中文保真失败 |
| 截断副本 | 仅复制 Unicode 文件头 512 字节 | 明确报错，进程返回 2，无挂起；原 PST 保留 |

RTF 使用压缩数据 CRC 验证并复位解压流后读取。早期“RTF 缺失”属于对照工具流位置错误，修复后两个格式均通过，**不计为库的保真缺陷**。非邮件项目明确识别为 IPM.Contact，没有当邮件导入。

反复读取/热缓存、Windows x64 .NET 10 环境下，Unicode 全量目录/正文/附件哈希检查约 1.03 秒、进程峰值工作集约 46.5 MiB；ANSI 约 0.16 秒、38.2 MiB。这不是多 GiB 文件或冷盘性能承诺。原 PST 与读取副本哈希一致，读取前后不变：

- Unicode：`DEC6709A10F457E7326F27DC68CC6BF4DBB0CEFC1E100DE46F6CF4BDD5ED4A5A`
- ANSI：`BCFCEB0311C78D7C3E5A05E223C0C780D355A0CF1D663244B86FED1E6F6D92E5`

解析源固定 `Dijji/XstReader@3b7856c8b3d01bdf8aa13744e476d1ef7761b832`，Ms-PL；编译出现 4 处上游 CA2022 不完整读取警告。它目前仅用于探测工具，不进入产品安装包。结论：可继续作为 Unicode 路线候选；ANSI 编码/长度问题和读取警告需要修复验证，或比较另一条可分发路线后才能定案。不购买商业库、不自动扩展为 Outlook 适配插件。50 保持进行中，51/52/53 不据此提前解除依赖。

## 复现与资源复用

```powershell
./scripts/start-greenmail.ps1  # 复用 artifacts/tools 的固定 jar 与已有 Java
dotnet run --project tests/MyMailBox.Checks -- protocols protocol-live protocol-faults
dotnet run --project tests/MyMailBox.UiChecks -- protocols  # 仅最终集中界面阶段
./scripts/inspect-outlook.ps1  # 不启动 Outlook
./scripts/prepare-pst-probe.ps1 # 固定源码缓存存在时不下载
./tools/MyMailBox.PstProbe/bin/Debug/net10.0/win-x64/MyMailBox.PstProbe.exe artifacts/outlook-acceptance/final/read-unicode.pst artifacts/outlook-acceptance/final/unicode-report.json
python scripts/check-pst-fixtures.py # 当前因 ANSI 保真失败返回 2，不能改写为成功
```

已有 PST 直接复用。只有需要重新生成样本时才在最终阶段执行 `create-outlook-fixtures.ps1`；它要求经典 Outlook 已退出，并使用全新产物目录。GreenMail jar 与 XstReader 源码各首次下载一次，Java、NuGet 已有依赖、.NET/WebView 和覆盖率工具均复用；没有下载或操作真实邮箱数据。
