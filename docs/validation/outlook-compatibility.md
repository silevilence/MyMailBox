# Outlook 发现与 PST 路线边界

本机只读发现：经典 Outlook `16.0.20527.20196`，PE Machine `0x8664`，Click-to-Run x64；新 Outlook `1.2026.1103.200` 同时安装。初始未发现经典 Profile，也没有 Outlook 进程。不会读取新 Outlook 的登录缓存；“已登录个人账户”不能推出经典 OOM 存在可枚举的已登录账户。

## 配置读取

| 路径 | 可以发现的范围 | 限制 |
| --- | --- | --- |
| 安装注册表、PE、Appx | 经典/新版、安装位置、版本、真实二进制位数、Profile 名称 | 不代表已读取邮箱配置；不启动 Outlook |
| 经典 OOM | CurrentProfileName、Accounts；Account 的 DisplayName/AccountType/SmtpAddress/UserName/DeliveryStore；Store.FilePath | 不是完整服务器配置接口；不能仅枚举地址就宣称配置导入；会进入 Outlook 会话，只在独立无账户 Profile 验证 |
| Account Management API | IOlkAccountManager/IOlkAccount 的公开属性与账户变更通知 | 需要按提供者实际暴露字段验证；不读取密码，不把 Outlook 自身授权令牌当应用授权；本轮没有个人经典账户样本 |
| Extended MAPI | Profile/service/provider 属性与 Store，支持更底层的读取 | 必须匹配 Outlook MAPI 位数；当前 x64 应用不能直接加载 x86 MAPI DLL |
| 新 Outlook | 本轮仅识别安装与版本 | 不使用经典 COM 路径，不枚举私人登录缓存；后续应通过提供者正规授权接入 |

Windows x64 交付保持不变。32 位 Outlook 的基础安装发现可做，但完整 MAPI 配置读取若需要 x86 helper，属于规格要求的待决定项，本轮不擅自引入、不宣称已有跨位数兼容实测。重新授权是在线账户导入的必要步骤。

## PST 路线比较

| 路线 | 优点 | 限制／分发 |
| --- | --- | --- |
| 已安装经典 Outlook 的 OOM/MAPI | 可创建真实 ANSI/Unicode PST 作为对照，读取 Outlook 规范化后的属性、正文和附件 | 依赖 Office 安装/用户会话/位数；不分发 Office DLL；Outlook 打开存储可能修改 PST，不能当原始文件只读解析器 |
| XstReader 固定源码 | 无 Office 依赖的 C# 读取；可枚举目录、属性、附件及嵌入邮件；可在 .NET 10 x64 编译 | 上游 Ms-PL，分发或修改须保留对应许可声明；目前仅验证工具，不加入产品安装包。完整 MIME 原文不一定存在于 PST，需明确重建与损失 |

初轮工具直接引用缓存提交，曾有 4 处上游 CA2022 警告。后续已增加本地可审阅修订，修复 ANSI 代码页、字符串边界、RTF 正文和短读路径，构建 0 警告；ANSI 18 项、Unicode 51 项对照通过。缓存上游仍保留未修改，编译使用明确排除/替换的修订文件。最新结论见 [50 修复与选型报告](2026-11-17-pst-ansi-fix.md)。另发现 `Crypto.cs` 含 libpff LGPLv3+ 来源声明，不能把整个组合视为只有 Ms-PL；产品分发前须落实该部分条件或替换实现。

PST 不天然保存一封邮件的原始 RFC 822 字节。可保留原文件并读取 MAPI 属性；生成 EML 应标为重建，不能宣称字节级原文保真。非邮件项目必须报告，不静默当作邮件导入。

## 官方依据

- [Outlook API 选择](https://learn.microsoft.com/en-us/office/client-developer/outlook/selecting-an-api-or-technology-for-developing-solutions-for-outlook)
- [Account Management API](https://learn.microsoft.com/en-us/office/client-developer/outlook/auxiliary/about-the-account-management-api)
- [MAPI 32/64 位边界](https://learn.microsoft.com/en-us/office/client-developer/outlook/mapi/building-mapi-applications-on-32-bit-and-64-bit-platforms)
- [无邮件账户运行 Outlook](https://support.microsoft.com/en-us/outlook/use-outlook-without-an-email-account)
- [AddStoreEx](https://learn.microsoft.com/en-us/office/vba/api/outlook.namespace.addstoreex)、[OlStoreType](https://learn.microsoft.com/en-us/dotnet/api/microsoft.office.interop.outlook.olstoretype)
- [XstReader 固定提交](https://github.com/Dijji/XstReader/tree/3b7856c8b3d01bdf8aa13744e476d1ef7761b832)、[许可](https://github.com/Dijji/XstReader/blob/3b7856c8b3d01bdf8aa13744e476d1ef7761b832/license.md)

最终实测证据在本轮集中验收报告中补充；此文不把静态接口调查当作个人账户或 32 位 Office 的实测。
