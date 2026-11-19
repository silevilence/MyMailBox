# MyMailBox 插件开发

桌面版在 Windows x64、.NET 10 JIT 上运行。引用与宿主相容的 MyMailBox.Contracts 和 CordiSharp，在类上声明 Plugin 入口和 Inject("mailbox.host")，实现 IPlugin<PluginOptions>。在 Load 中取得 IPluginHost，用 context.Effect 注册贡献项并返回 IDisposable。最小可运行例子见 [ExamplePlugin](../samples/ExamplePlugin/ExamplePlugin.cs)，邮箱例子见 [MailboxPlugin](../samples/MailboxPlugin)。

## 安装和选择

运行 `dotnet build MyMailBox.slnx` 和 `./scripts/package-samples.ps1`。产物位于 artifacts/packages。ZIP 根目录包含 manifest.json，声明 id、version、sdkMin、sdkMax、kind、entry、assembly、permissions 及各私有文件的 SHA-256。宿主检查 SDK 范围、文件和依赖。使用宿主已有的共享契约和 CordiSharp，样例邮箱也复用宿主 MimeKit。随包分发需要的私有依赖。

安装插件包后，在工作空间的 plugins.json 配置普通实例。每个实例设置唯一 id、plugin、version、enabled、grants。注册 ID 通常为「实例 ID/贡献项 ID」。引用和授权针对稳定 ID，同一显示名可以属于不同实例。修改普通插件配置会触发重新加载。核心实例写入 core.json，在下次启动生效。例如将下面的实例追加到已有 instances 中，保留已有核心平台实例。

```json
{"id":"file-store","plugin":"example-file-store","version":"1.0.0","enabled":true,"grants":["mail.storage"]}
```

重启后，在“本地邮箱”可导入 EML 并按原文字节导出。这个示例接管本地阅读页的存储和解析，持久化 EML 与摘要。账户索引、归档任务及计划仍使用各自的工作空间数据库。移除核心实例配置后重启，可重新打开原有 SQLite 邮箱。普通更新源使用 example-feed，授权 updates.check，在“软件更新”中选择“离线示例更新源”并保存，再点“立即检查”。停用已选更新源后可在此更换。下载、签名校验和安装由宿主执行。

## 已接入的公开契约

邮箱插件的增量页中，只有从请求游标起该邮件的所有变动都保留原文时，才设置 `MailboxItem.OriginalUnchanged`。仅已读标记变化可复用已下载的 MIME。新增邮件、正文变更、混合更新和无法判断的变化使用默认值 `false`。

| 场景 | 注册与样例 | 验证入口 |
| --- | --- | --- |
| 账户类型、连接、目录、收发和远程状态 | AddMailboxProvider；example-mailbox 声明各项能力并使用本地回环身份。进入账户管理、写信、邮箱浏览和统一收件箱。需要授权的提供者在 Connect 中完成自己的授权流程。 | mailbox-provider、protocol-live、graph、exchange-eas |
| 本地邮件存储和 MIME 阅读 | 核心插件 AddMailBackend；example-file-store 与内置 SQLite 切换 | extension-matrix |
| 文件导入 | AddImportReader；example-importer 提供参数、流式逐项回调和损失报告 | import-plugin、pst-resume |
| 导出 | AddExporter；example-exporter 与内置 EML、附件和元数据格式共享选择入口 | export、bulk-export |
| 搜索字段和运算符 | AddSearchExtension；example-search 按账户授权处理限定批次，字段 ID 包含版本 | search-extension |
| 规则、条件、动作、调度 | AddAutomationExtension；automation-example 的两版包验证迁移、暂停和恢复 | task-plugins |
| 编辑、地址和选择器 | AddEditor、AddAddressProvider、AddSelector；example-editor、example-addresses、alternative-selector | drafts、address-book、selectors |
| 通知 | AddNotificationPresenter；example-presenter 在活动中心接收已应用隐私设置的摘要 | extension-matrix、activity |
| 更新源 | AddUpdateSource；example-feed 通过标准更新设置选择 | extension-matrix、update-checks |

## 调用、版本和停用

声明的能力还须在实例配置授予，可按账户限制的能力支持 `mail.read:<账户 ID>`。服务端未授权、未登录、缺少权限、离线和取消须返回相应错误，跨账户读写使用调用所给账户。连接、列举和预览不得隐式发送邮件。能力标志决定哪些可选操作对用户开放。未实现的操作抛出 NotSupportedException。外部发送使用持久化操作 ID，回执无法确定时返回待核对结果。

读取原文时向调用方提供的 Stream 写入，上传或发送时从调用方的流读取，流所有权属于调用方。分页游标只对原账户、目录和实现有效。磁盘操作和网络等待传递 CancellationToken，逐项导入在宿主确认保存后推进进度。格式、动作参数和搜索字段有独立版本，升级时提供兼容读取或迁移，拒绝未知版本。

插件和宿主同进程、同 Windows 用户权限运行。只安装可信来源。哈希检查用于发现损坏，宿主授权限制的是通过契约进行的调用。停用时宿主停止接单，等待或取消在途操作，解除事件和贡献项，释放目标、CordiSharp 句柄和可卸载程序集。插件须释放定时器、订阅、网络资源及自建任务，避免静态缓存持有可卸载类型。需要保留状态时使用工作空间的 plugin-data 实例目录。可用 `dotnet run --project tests/MyMailBox.Checks -- extension-matrix` 在隔离数据中复验。
