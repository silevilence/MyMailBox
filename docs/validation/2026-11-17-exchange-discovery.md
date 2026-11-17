# 35：公司 Exchange 端点初步验证

最新结论：EWS 自动协商及显式 NTLM 只读认证均返回 401，用户补充域账号和完整邮箱地址均已试过。手机邮件应用能读列表，新增 ActiveSync 路线调查：匿名 OPTIONS 返回 401/Basic，已交付仅查询能力的认证探测脚本，待用户本机执行。35 保持 blocked，不能据此认定 EWS 或完整收发已通过。

用户提供 `https://mail.example.invalid/owa` 并授权先做不影响服务的检查。2026-11-17 13:07、13:31（Asia/Shanghai）各执行两次普通无凭据 GET：一次 OWA，一次标准 EWS 路径。无扫描、口令尝试或自动重试。

| 请求 | 结果 | 能证明的范围 |
| --- | --- | --- |
| `/owa` | 302 至同主机 `/owa/auth/logon.aspx` | OWA 登录入口可从本机外网到达 |
| `/EWS/Exchange.asmx` | 401，`WWW-Authenticate: Negotiate, NTLM` | 存在要求认证的 EWS 路由；服务器声明这两种认证挑战 |
| HTTPS | 系统 TLS 验证通过，无证书绕过 | 此时此机的域名/证书连接验证通过 |

证据保存在 `artifacts/exchange-discovery/unauthenticated.json`、`probe.json`。IIS 10.0 不是 Exchange 产品版本，认证挑战也不能证明某个账户有权限。单凭这些响应不能认定邮箱在本地、云或混合中的实际位置，更不能宣布完整 EWS 读写通过。

## 下一步只读登录

已交付 `scripts/probe-exchange.ps1`：匿名模式仅两次 GET；提供凭据时只执行一次 EWS `GetFolder`（`msgfolderroot`，`IdOnly`）。禁止跟随重定向，不使用当前 Windows 身份自动登录，不读取正文、不发送、不修改邮件。密码只在本机 `Get-Credential` 输入，不通过聊天或命令参数传输。结果只保存 HTTP 状态、认证方式和脱敏版本/响应代码，不保存用户名、原始响应、目录 ID、cookie 或 token。

```powershell
./scripts/probe-exchange.ps1 -OwaUrl https://mail.example.invalid/owa -PromptForCredential -Output ./artifacts/exchange-discovery/authenticated.json
```

用户已表示可以输入凭据；代理尝试打开本机交互终端时，命令策略返回 `blocked by policy`，进程未启动。不能据此声称是 Windows 权限错误或自动审批审核拒绝。已将以上命令交给用户在本机运行，等待结果；没有换解释器规避。

后续实际结果：用户在本机执行，自动协商在 **05:41:10 UTC** 返回 401；确认输入为“域名\用户名”且网页登录正常后，增加明确选择服务器声明支持的 NTLM，第二次在 **05:45:48 UTC** 仍返回 401。证据为 `authenticated.json` 和 `ntlm.json`。两次均未获得邮箱根目录，已停止认证重试。不能从这些结果判断密码错误，也不能证明 EWS 已获账户授权。

需要 IT 核查的具体信息：对应时刻 IIS 日志的 `sc-status/sc-substatus/sc-win32-status`，外网反向代理对 EWS 的 Windows 认证支持及 VPN 要求，账户的 EWS 启用/应用访问策略，以及真实邮箱部署位置。不要通过关闭扩展保护、降低 TLS 或猜测认证方式解决。无需向项目提供密码或原始含身份日志，只提供脱敏结论。

离线 XML 响应解析与 DTD 拒绝测试已通过：

```powershell
./scripts/probe-exchange.ps1 -OwaUrl https://mail.example.invalid/owa -SelfTest
```

即使只读登录通过，35 仍需实际部署信息以及指定测试目录中的正文/附件、测试发送、已读和移动验收。真实写入/发送尚未执行；准备独立测试邮件及目录后再落实范围，不能对现有业务邮件试操作。当前保留 blocked，已有端点证据及可复用脚本。

## 手机成功后的 ActiveSync 路线

用户补充：手机 Exchange 邮箱以同一主机、SSL/TLS 和完整邮箱地址成功读取列表；原 EWS 脚本的“域名\用户名”和完整邮箱地址均为相同失败结果。因此不再把账号格式视为已知修复方式。手机可能使用 EAS，但截图本身不能确认具体协议。

2026-11-17 **06:11:48 UTC** 单次无凭据 `OPTIONS /Microsoft-Server-ActiveSync`：系统 TLS 验证通过，返回 **401 / Basic**，未返回协议版本或命令。证据复用 `artifacts/exchange-discovery/activesync-options.json`。这与 EWS 的 Windows 认证挑战不同，值得独立验证；不等于已认证成功或确认 Exchange 产品版本。

已准备 `scripts/probe-activesync.ps1`，仅发送一个逻辑 OPTIONS 操作（包括 HTTP 认证挑战处理），Basic 凭据仅绑定对应 HTTPS 端点，不预发送、不跟随重定向、不使用 Windows 默认身份或 cookie；20 秒超时，不自动切换认证或重试登录。无 DeviceId、DeviceType、命令正文或策略键，不发送 Provision/FolderSync/Sync/SendMail。不会主动建立设备同步关系、读取或修改邮件；正常服务端访问/认证日志仍可能产生。只输出状态、认证方案、协议版本和命令名称，不读取响应正文或输出身份/凭据。

用户在自己的 PowerShell 执行，使用手机成功登录时相同的身份与密码：

```powershell
& 'C:\code\dotnet\MyMailBox\scripts\probe-activesync.ps1' -ServerUrl https://mail.example.invalid -PromptForCredential
```

脚本离线 `-SelfTest` 已通过版本/命令解析、401 与缺失头判定、cookie/realm 不泄漏检查，无网络请求。尚未执行真实带凭据 OPTIONS；代理此前的交互凭据终端启动受命令策略限制，继续由用户本机输入，不绕过限制。

结果解释：200 且返回版本/命令表示 OPTIONS 能力查询成功，不能代替邮箱访问授权与收发验收；401 仍是认证未通过，403 需检查服务端策略；重定向不自动跟随。EAS 14.x/16.x 是协议版本，不是 Exchange 2016/2019 等产品版本。

依据：[OPTIONS 响应规定](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-ashttp/bc92056f-5c48-4775-9f0d-b16b86998e55)、[ActiveSync 认证](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-ashttp/7b7fabb9-910c-4f1c-9396-57d7ca579a31)。
