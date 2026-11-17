# 本地邮件与阅读

原始 MIME、稳定 UUID、摘要与目录保存在工作空间 mail/mail.db。SQLite WAL 事务保证原文、元数据与索引同时提交；列表按 50 封分页，正文按需解析。存储版本 0→1 在事务中迁移，未来版本拒绝写入。完整性检查验证 SQLite、外键和 SHA-256；可丢弃缓存与唯一原文分表，重建不会改写原文。

阅读限制：EML 25 MiB、MIME 32 层/2048 部件、纯文本与 HTML 各 1 Mi 字符；原文预览 128 KiB，完整字节始终保留供 EML 导出。内联图片单项 2 MiB、总量 10 MiB，支持 PNG/JPEG/GIF/WebP。超限会明确提示，不伪造完整显示。

HTML 经 AngleSharp 解析与允许列表清理，移除脚本、表单、frame、远程样式/字体、事件属性与危险导航，追加 CSP。独立 WebView2 没有宿主对象或编辑器桥接，脚本与 web message 关闭，权限/弹窗/下载拒绝；WebResourceRequested 仅返回当前随机隔离来源的文档及声明资源。外部链接先在宿主显示 URL，用户确认后交给浏览器。纯文本/原文可随时回退。

依据：[MimeKit 深度限制](https://mimekit.net/docs/html/P_MimeKit_ParserOptions_MaxMimeDepth.htm)、[SQLite 异步限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)、[WebView2 Controller](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createcorewebview2controllerasync)。数据库同步 I/O 在后台任务中执行，不占用 UI 线程。

工作空间备份通过 SQLite 在线备份 API 取得一致快照，包含配置/插件/插件数据，移除账户凭据并要求重新授权。邮件归档包仅含选定范围的账户说明/标签、目录、元数据、原文和哈希；导入为独立本地邮箱，冲突重新映射，失败整笔回滚。两者不可混用。

批量导出先按账户全部目录或单个目录和主题筛选生成稳定邮件 ID 预览；后续新增邮件不悄悄加入。CSV/JSON 默认导出 ID、主题、日期和大小，地址与附件名称需明确勾选，不导出正文或凭据。CSV 对公式起始字符及前导空白转义。附件按扩展名筛选，可选内联内容，命名模板支持主题、原名和 ID；按邮件 ID 分目录，重名另存，失败/缺失/取消逐项列出。取消保留已完成结果。

导出格式通过 IMessageExporter 注册，外部 example-exporter 示例包贡献 JSONL 主题列表，同一导出向导自动显示。导出器卸载排空在途操作并清除实现引用；示例只序列化共享类型，避免通用 JSON 缓存持有可回收程序集类型。
