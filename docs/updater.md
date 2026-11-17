# 更新组件与发布链路

2026-11-17 核实 `git remote` 为 `https://github.com/silevilence/MyMailBox.git`，仓库公开。客户端默认更新配置应来自该地址，不使用个人 token；测试可注入第二源。

## 组件选择

选择 [NetSparkleUpdater.SparkleUpdater 3.1.0](https://github.com/NetSparkleUpdater/NetSparkle/releases/tag/3.1.0)，官方稳定发布包含 .NET 10 支持。NuGet flat-container 版本清单已核对，后续 3.1.1 / 4.0.0 preview 不作为稳定能力。只依赖核心包，沿用宿主 Avalonia UI 与 NSIS，不引入上游 UI 包或运行时捆绑。

[Octokit 14.0.0](https://www.nuget.org/packages/Octokit/14.0.0) 是 GitHub API SDK，不提供安装、签名校验或应用排空。此次更新只需少量公开 Release GET 请求，使用 HttpClient 完成发现与缓存，NetSparkle 负责签名 appcast、软件下载和安装接口。没有另建更新服务。

NetSparkle 读取 appcast，不直接读取普通 GitHub Releases。发布流程须上传安装包、`appcast.xml` 与 `appcast.xml.signature`；enclosure 内再携带安装器 Ed25519 签名。客户端固定可信公钥，以 Strict 模式检查。测试私钥仅在测试进程内随机生成；正式私钥应由 CI Secrets 提供，不能从下载的清单建立新的信任根。未配置正式公钥时可检查版本，但不得执行自动下载安装。

上游 3.1.0 的 [SemVerLike](https://github.com/NetSparkleUpdater/NetSparkle/blob/3.1.0/src/NetSparkle/AppCastHandlers/SemVerLike.cs) 把 `+build` 后缀参与排序，因此通过 IAppCastFilter 改用 NuGet.Versioning 7.0.3 的 VersionRelease 比较器，忽略构建元数据、按数字比较 prerelease，并在入口校验严格三段 SemVer 与 V/v 前缀。稳定通道排除 prerelease。该适配保留 NetSparkle 的签名/平台筛选。

## 本地验证与验收边界

`dotnet run --project tests/MyMailBox.Checks -- updater-spike` 已通过真实 3.1.0 包的签名 appcast 解析、本地文件下载、相同已验证文件复用、篡改安装器拒绝、篡改 appcast 返回无法检查、宿主退出否决及 SemVer 边界。测试载荷是随机字节，**没有将它当作可运行安装包或声称真实升级通过**。

本轮末集中使用两个本地 NSIS 应用版本完成实际升级；复用本机 SDK、运行时、NSIS、WebView2 与已生成产物。安装阶段须先保存工作并排空插件，退出失败不启动安装；新版启动/迁移失败保留诊断和独立工作空间备份，回滚程序文件不能代替数据恢复。正式 GitHub Release/appcast 发布仍属于任务 92 的真实验收。

## 检查设置

内置普通插件 `updates` 提供“软件更新”页面，可以停用/卸载、换源。首次安装从随应用分发的 `update-source.json` 读取真实仓库；用户设置保存在工作空间 `plugin-data/updates/updates.json`。后台检查默认关闭，启用后按 1–168 小时间隔检查；完全退出时不运行。手动检查有 30 秒最小缓存，后台默认 6 小时，ETag/304 复用响应，限流按 Retry-After / X-RateLimit-Reset 等待。重启保留检查结果和跳过版本，应用版本改变清除旧结果。

稳定通道同时过滤 GitHub prerelease 标志与 SemVer 预发布后缀，draft 不参与。只选择对应版本 win-x64 安装器、appcast 及签名全部齐备的版本；发现新版本但缺少资产单独显示，网络/代理/超时/限流分别给出可理解反馈。变更说明按纯文本显示，不执行 Release 中的 HTML。检查使用系统代理，无个人 token。

`update-checks` 定向组覆盖 HTTP 模拟、持久化、限流/缓存、替换源和插件停止后的旧调用拒绝；`update-live` 单独读取真实公开仓库，不加入默认离线测试，不下载任何 Release 资产。2026-11-17 实际读取 silevilence/MyMailBox 返回空 Release 列表，与“没有更新版本”结果一致。

## 下载与安装交接

“下载并校验”使用 Strict 签名 appcast 中的版本、URL、长度和 `urn:mymailbox:update:v1` 命名空间下的 sdkVersion，必须与选定 Release 资产一致；已验证的本地文件复用。下载内容存于工作空间 updates/downloads，不进入正常工作空间备份。公钥为空时仅允许版本检查。下载中可取消；进入 Ready 前及用户点击“安装更新并退出”时重新检查文件长度、SHA256、Ed25519 和版本递增关系。

更新前宿主先检查启用的外部插件 SDK 范围，依次调用 AddUpdatePreparation 注册的保存参与者，再停止配置监听、排空插件和后台计划，创建 updates/backups 下的一致性备份。任何一步失败不启动安装；若部分插件已停止，提示关闭后重启。真实写信草稿/发件箱能力尚未实现，不能以保存参与者测试声称这些数据已验收。

MyMailBox.UpdateHost 与主程序采用相同的 .NET 10 win-x64 framework-dependent JIT 发布方式。宿主把助手复制到工作空间独立目录，避免锁住安装目录 DLL。助手等待精确父进程退出（最多 90 秒）、再次验签，以直接进程调用执行 NSIS `/S /D=<原安装目录>`，不通过 shell 拼接命令；成功后检查运行时并启动新应用。安装执行与网络检查分层。升级状态写入 updates/upgrade-state.json，包含备份路径；主程序核对目标版本、配置和工作空间初始化后才确认完成。失败不删除邮件/插件数据、不自动回滚数据库；从备份恢复到新工作空间，在线账户可能需要重新授权。

`update-install` 定向组不运行安装器，验证签名/SDK/哈希/重启/取消与失败边界。真实安装升级单独列在最终实机记录中；正式公钥和 Actions 签名 Secrets 尚未准备，不应把当前开发构建当作已部署的自动更新通道。
