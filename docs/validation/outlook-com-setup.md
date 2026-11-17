# Outlook.com 个人账户接入配置

本项目现在具有只读 Graph 接入实现。真实验收仍需要自己的公共客户端应用注册和个人账户交互授权，已有 Outlook 登录不提供本项目 Client ID，也不会提取 Outlook token。

## 注册与使用

1. 在 [Microsoft Entra 应用注册](https://entra.microsoft.com/) 创建自己可管理的应用。支持的账户类型须包含个人 Microsoft 账户；只有组织账户的注册不适用本任务。若账号无法进入注册页面或没有创建应用权限，需要有权限的租户/应用所有者提供注册，不能绕过租户要求。
2. 添加“移动和桌面应用”平台，系统浏览器回调 URI 为 `http://localhost`。使用公共客户端，不创建客户端密钥。
3. 添加 Microsoft Graph **委托权限 `Mail.Read`**。本轮不申请 `Mail.ReadWrite`、`Mail.Send`；MSAL 处理登录与刷新所需的 OpenID Connect 权限。
4. 复制“应用（客户端）ID”。它是 GUID，不是秘密，不是租户 ID。
5. 在 MyMailBox“账户管理”添加“Outlook.com（个人账户，只读）”，地址填写实际微软登录地址。然后打开“Outlook.com 邮件”，粘贴并保存 Client ID，点击登录，在微软官方页面自行完成密码/MFA与同意。别把密码/token 发到聊天。
6. 点击读取根目录，选择目录读取摘要；可进入子目录及翻页。下载所选邮件后，到“本地邮件”中该账户的“Outlook.com 已下载”目录查看正文、原始 MIME 和附件。只执行远程 GET，不改变已读或其他云端状态。
7. “断开本应用授权”清除本应用的本地 token 缓存，保留下载邮件；不会退出已有 Outlook。它不是撤销微软端的授权同意，需要撤销时在微软账户应用权限页操作。

缓存使用 Windows 当前用户 DPAPI 加密。移除账户清理缓存，换 Client ID 需要重新登录，可移植备份不包含 token。授权身份与本地地址绑定，登录另一个账户会被拒绝。存在多个登录别名时，填写微软登录结果报告的登录地址；本轮未实现别名自动映射。

## 当前验证边界

`dotnet run --project tests/MyMailBox.Checks -- graph` 覆盖配置、缓存、备份、身份隔离、分页、MIME/附件、401 刷新、拒绝/取消/网络错误及断开清理，使用本地替身，不代表微软真实授权成功。缺少 Client ID 时不会拿别的产品应用 ID 试登录。

完整实现与集中验收证据见 [32 本地验收](2026-11-17-graph-read.md)。

目录/摘要是按需分页，不是增量同步；下载为用户选择的本地快照。发送和自动同步分别属于 33/34。邮件读取沿用本地 25 MiB 限制，超限明确提示。

依据：[桌面应用配置](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration)、[MSAL 浏览器及回调](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers)、[个人账户 authority](https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-application-configuration)、[Mail.Read 权限](https://learn.microsoft.com/en-us/graph/permissions-reference#mailread)、[固定 MSAL 版本 4.92.0](https://www.nuget.org/packages/Microsoft.Identity.Client/4.92.0)。
