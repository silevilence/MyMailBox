# Exchange ActiveSync 候选路线

2026-11-19。公司端点已有一次带凭据 OPTIONS 200 的记录，最高声明版本为 16.1。本轮使用本机 HTTP 替身与独立 WBXML 字节样本实现并检查客户端。公司部署、设备准入、邮箱授权和实际读写仍待验证。[公司端点调查](2026-11-17-exchange-discovery.md)

| 入口 | 实现与本地验证 |
| --- | --- |
| 账户管理 → 公司 Exchange（ActiveSync） → Exchange 连接 | 输入管理员提供的 HTTPS 端点和登录身份，密码使用 Windows 当前用户 DPAPI。检查能力使用 OPTIONS，随后按账户保存版本、命令和稳定设备标识。 |
| 邮箱浏览与整理 | 勾选设备同步并保存、检查能力后，可连接、查看层级目录、翻页、下载完整 MIME 和附件、回复或转发。服务器要求 Provision 或拒绝设备时停在可诊断状态，由管理员核对。 |
| 统一收件箱 | 手动或后台同步收件箱。同步键与变化按页提交；首次同步和失效键重建在独立暂存表进行。完成后替换本地摘要，已下载原文可离线阅读。 |
| 写邮件与草稿 | 勾选允许写入并检查能力后，发送、回复和转发复用持久发件箱。回执丢失时需要核对服务器。16.0/16.1 的草稿副本可携带收件人、密送、正文和附件；产生的本地版本记录可阻止重复提交。 |
| 邮箱浏览与整理 → 标记、移动 | 按当前同步键提交已读变更；移动采用服务器返回的目标邮件标识，保留本地已下载的完整副本。 |

非 UI 检查入口为 `dotnet run --no-build --project tests/MyMailBox.Checks -- exchange-eas`。用例覆盖无设备授权、401、重定向、449、目录与完整 MIME、附件、断网续页、同步键过期、部分字段、已读、移动后阅读、云端草稿确认及发送回执丢失。真实 Exchange 操作应使用另行指定的测试邮箱与目录。

实现依据包括微软的 WBXML 代码页表、目录同步、MIME Fetch、带附件草稿和 SendMail 响应定义。[WBXML 代码页](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-aswbxml/2e57ccd4-cf38-45a0-bb11-95304bd1180d) · [FolderSync](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-ascmd/62005264-69bd-4256-8fc9-00a33862eb6d) · [获取原文](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-ascmd/30efdae3-8a02-4f6d-aa8c-ab057db8240f) · [带附件的草稿](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-asemail/2a75ece7-b6bb-4843-a23d-075720fae310) · [SendMail](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-ascmd/172db9ec-350f-4bb1-94b1-0526f976ed5e)
