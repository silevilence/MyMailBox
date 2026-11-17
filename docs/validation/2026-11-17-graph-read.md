# 32：Outlook.com 只读接入实现及本地验收

## 结论

只读服务、账户插件接入和宿主界面已实现。本地自动化及集中桌面验收通过；**尚无本项目 Client ID，未完成真实个人微软账户授权和云端邮件验收，因此 32 保持 in-progress**。用户已获知一次性应用注册要求及[操作步骤](outlook-com-setup.md)。不借用其他产品 Client ID，不读取 Outlook 登录缓存。

## 实现边界

- 固定 Microsoft.Identity.Client 4.92.0，公共客户端、系统浏览器及 `http://localhost` 回调；authority 固定 `consumers`，只请求 Graph `Mail.Read`。没有客户端密钥、发送、改已读、移动或删除能力。
- 每个本地账户使用独立 MSAL 缓存引用，Windows DPAPI 保护，移除或断开时清除；更换 Client ID 重新授权，可移植备份不含 token。账号地址及 home account ID 绑定，错账号登录被拒绝并清除其本地缓存。
- Graph GET 读取根/子目录、分页摘要、指定邮件 MIME；保持云端状态。分页链接限定 HTTPS 官方 Graph 主机和当前资源路径，拒绝跨主机、跨资源及重定向。401 最多强制刷新一次，拒绝/限流/网络/超时/取消可重新操作，不自动重发写操作。
- 原始 MIME、元数据和来源记录同事务保存到当前账户的“Outlook.com 已下载”，原文及附件通过现有本地阅读器查看；相同来源保持本地 ID。此功能是按需下载快照，不冒充增量同步。单封沿用 25 MiB 限制。
- 作为账户内置插件的独立页面注册；共享契约及 PluginScope 包装支持取消、停止接单与在途释放。通用 IMAP/SMTP/POP 界面排除 Graph 账户。
- 字段有明确标签，结果和取消入口固定；操作期间禁用冲突操作。集中 UI 发现作用域包装把输入异常转为 InvalidOperationException 导致提示被吞，已修复为显示服务提供的可恢复提示。

## 验证与证据

1. `dotnet run --project tests/MyMailBox.Checks -- graph`：实际 DPAPI 缓存/重开/清除/备份，身份隔离，目录子目录/分页/外部 nextLink 拒绝，MIME 与附件，401 刷新、403/404/429/5xx/重定向、授权拒绝/取消/网络错误及断开/移除检查通过。Graph/身份响应为本地替身，不是微软授权证据。
2. `scripts/verify.ps1` 完整非 UI 回归和框架依赖发布启动检查通过：`artifacts/graph-acceptance/verify-recheck.log`。首轮曾在既有插件升级检查遇到一次程序集保留失败；单独 `upgrade` 和完整复跑均通过，保留初轮日志，不把它隐去或声称已定位修复该偶发现象。
3. 最终构建 0 警告、0 错误；Graph/账户/协议定向回归通过：`build-final.log`、`targeted-final.log`。发布复用本机运行时及缓存：`publish-final.log`。
4. 新增 FlaUI 批次 `-- graph` 验证无 Client ID 提示、非法 ID、保存/重开、未授权恢复、目录/邮件未选提示、断开及协议账户隔离。证据：`ui-final.log`、`ui-20261117-135056/`。界面没有点击有效 ID 的真实登录，也没有触碰个人账号。
5. 完整既有 FlaUI 通过：本地阅读/附件/导出、账户/选择器/归档、提醒/CRON/更新设置、插件升级回滚、配置修复、30 次导航卸载、诊断及安全启动。证据：`full-ui.log` 与 `full-ui/`。测试应用正常退出。

尚待真实验收：微软页面取消/拒绝及重新登录、个人账户目录/带附件邮件、令牌刷新与撤销、实际别名行为。注册配置步骤不能替代这些验证。33/34 未提前标完成。
