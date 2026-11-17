# 本地协议与 Outlook/PST 集中验收计划

范围：20、22、31、48、50。32、35 按用户要求暂缓。账户与目录依赖 11/12/13/18 已完成。

## 准备与隔离

- 使用已有 Android Java 21；GreenMail 2.1.15 jar 首次下载后复用，SHA-256 `91D58798698CAC357F015365240383B936CED2F2A1A9F7DB37D9E3E1A31391B1`。仅绑定 127.0.0.1，不配置外部转发。
- MailKit 固定 4.17.0，与已有 MimeKit 4.17.0 保持一致；复用 NuGet 缓存。TLS 测试证书仅在独立产物目录，不加入系统信任存储。
- MyMailBox 使用 artifacts 下独立工作空间。临时密码仅对应本机测试服务；不读取个人账号凭据。
- `inspect-outlook.ps1` 只读取安装信息、PE 位数和 Profile 名称。当前经典 Outlook 16.0.20527.20196 x64；新 Outlook 1.2026.1103.200；未发现经典 Profile，二者未运行。
- 最后阶段才启动经典 Outlook，使用 `/PIM MyMailBoxAcceptance…` 独立无账户 Profile。确认 Profile 名及 Accounts.Count=0 后才写入独立测试 PST；保留并恢复原默认 Profile 设置。若存在其他 Outlook 实例，不强制关闭它。
- 原始 PST 关闭后复制到只读解析样本目录，记录前后哈希。ANSI/Unicode、中文多层目录、HTML/CID、RTF、嵌入邮件、非邮件项目及扩容样本分别记录，损坏测试只使用副本。

## 一次集中验收顺序

1. 全部编译、轻量契约检查与测试脚本准备完成。
2. 启动 GreenMail 一次；真实 TLS SMTP 投递、IMAP 目录/分页/已读不变，POP3 下载/重启去重/来源删除；受控协议服务补充拒绝、部分接收、无 UIDL、断线、存储失败等场景。
3. 运行完整非 UI 回归；发布 win-x64 framework-dependent 产物。
4. 单次 MyMailBox FlaUI 批次：配置保存/重新打开、连接错误与恢复、目录分页、To/Cc/Bcc 发送结果、POP 下载及本地阅读、取消与停用释放，集中截图。
5. 单次经典 Outlook 测试 Profile 会话：核实 OOM 字段、制作 PST 和对照清单；释放 COM、关闭测试 Outlook、恢复原默认 Profile。
6. 离线读取 PST，核对目录、正文、附件哈希、嵌入邮件、异常项及吞吐/内存；原文件哈希不变。
7. 停止本轮创建的 GreenMail/测试进程；归档证据，完成审核与工单/index/coverage 更新。未覆盖项保持明确状态，不把合成服务等同于 QQ/163 等提供者认证验收。

## 依据

- [GreenMail](https://greenmail-mail-test.github.io/greenmail/)
- [MailKit 4.17.0](https://www.nuget.org/packages/MailKit/4.17.0)
- [Outlook 无账户 Profile](https://support.microsoft.com/en-us/outlook/use-outlook-without-an-email-account)
- [Outlook 接口选择](https://learn.microsoft.com/en-us/office/client-developer/outlook/selecting-an-api-or-technology-for-developing-solutions-for-outlook)
- [XstReader](https://github.com/Dijji/XstReader)，验证源固定提交 `3b7856c8b3d01bdf8aa13744e476d1ef7761b832`，Ms-PL；源码仅缓存到 artifacts，当前不是产品分发依赖。
