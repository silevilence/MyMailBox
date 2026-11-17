# Outlook.com 个人账户接入配置

本项目使用由发布者维护的公共客户端应用注册，所有个人账户用户共用同一 Client ID，各自登录和授权自己的邮箱。Client ID 是公开的应用标识，不是用户凭据。普通用户无需注册应用或填写 Client ID，不提取已有 Outlook token。当前注册仅支持个人 Microsoft 账户，不包括公司/学校组织账户。

## 普通用户使用

1. 在 MyMailBox“账户管理”添加“Outlook.com（个人账户，只读）”，地址填写实际微软登录地址。
2. 打开“Outlook.com 邮件”，选择账户，点击“登录微软账户”，在微软官方页面自行完成密码/MFA与同意。无需输入 Client ID，不要把密码/token 发到聊天。
3. 点击读取根目录，选择目录读取摘要；可进入子目录及翻页。下载所选邮件后，到“本地邮件”中该账户的“Outlook.com 已下载”目录查看正文、原始 MIME 和附件。只执行远程 GET，不改变已读或其他云端状态。
4. “断开本应用授权”清除本应用的本地 token 缓存，保留下载邮件；不会退出已有 Outlook。它不是撤销微软端的授权同意，需要撤销时在微软账户应用权限页操作。

## 发布者维护应用注册

下列操作只由开发者/发布者执行一次，不是普通用户登录步骤。真实应用 ID 不进入 Git，构建时注入程序集元数据，`GraphApplication` 读取该值。界面与账户服务契约不提供逐用户修改 ID 的入口。

- 本地：在根目录 `Directory.Build.local.props` 设置 MSBuild 属性 `MyMailBoxGraphClientId`，此文件已被 `.gitignore` 排除；当前本机已配置。不要强制添加此文件、`obj` 生成文件或构建日志到 Git。
- Actions：仓库 Secret 名称为 `MYMAILBOX_GRAPH_CLIENT_ID`，工作流通过同名环境变量注入；Tag 发布显式传递给复用构建，并在缺失/无效时停止。PR 不注入此 Secret。此次没有替用户设置远端 Secret。
- 普通源码构建允许缺少配置，其他功能照常使用，点击微软登录时明确提示联系维护者。本地替身测试可使用随机测试 ID，不要求真实注册。
- 构建产物含运行时必需的应用标识；以 Secret 管理的是源码与构建配置的分发，不使桌面客户端中的应用 ID 成为不可提取的凭据。

1. 在 [Microsoft Entra 应用注册](https://entra.microsoft.com/) 创建自己可管理的应用。支持的账户类型须包含个人 Microsoft 账户；只有组织账户的注册不适用本任务。若账号无法进入注册页面或没有创建应用权限，需要有权限的租户/应用所有者提供注册，不能绕过租户要求。
2. 添加“移动和桌面应用”平台，系统浏览器回调 URI 为 `http://localhost`。使用公共客户端，不创建客户端密钥。
3. 添加 Microsoft Graph **委托权限 `Mail.Read`**。可为后续功能在门户预配置 `Mail.ReadWrite`、`Mail.Send`，但本轮代码仍只请求 `Mail.Read`；门户配置不会使当前程序自动获得写入/发送能力，后续还需实现与授权同意。MSAL 处理登录与刷新所需的 OpenID Connect 权限。
4. 将“应用程序（客户端）ID”写入上述本地配置或 Actions Secret。它不是“对象 ID”或“目录（租户）ID”。2026-11-17 用户提供的注册已保留在本机，当前待完成真实交互登录与读取验收。

缓存使用 Windows 当前用户 DPAPI 加密，仍按邮箱账户隔离。移除账户清理缓存，可移植备份不包含 token。旧版逐账户 Client ID 仅作为缓存归属保留；与当前发布者注册不一致时不复用其授权，用户下次登录时清除旧缓存并使用当前应用 ID。授权身份与本地地址绑定，登录另一个账户会被拒绝。存在多个登录别名时，填写微软登录结果报告的登录地址；本轮未实现别名自动映射。

## 当前验证边界

`dotnet run --project tests/MyMailBox.Checks -- graph` 覆盖直接登录、多个用户共用注册且独立缓存、旧注册缓存迁移、备份、身份隔离、分页、MIME/附件、401 刷新、拒绝/取消/网络错误及断开清理，使用本地替身，不代表微软真实授权成功。

完整实现与集中验收证据见 [32 本地验收](2026-11-17-graph-read.md)。

目录/摘要是按需分页，不是增量同步；下载为用户选择的本地快照。发送和自动同步分别属于 33/34。邮件读取沿用本地 25 MiB 限制，超限明确提示。

依据：[桌面应用配置](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration)、[MSAL 浏览器及回调](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers)、[个人账户 authority](https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-application-configuration)、[Mail.Read 权限](https://learn.microsoft.com/en-us/graph/permissions-reference#mailread)、[固定 MSAL 版本 4.92.0](https://www.nuget.org/packages/Microsoft.Identity.Client/4.92.0)。
