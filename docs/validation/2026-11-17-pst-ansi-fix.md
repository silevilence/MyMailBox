# 50：ANSI PST 保真修复与选型结论

## 结果

复用上一批独立无账户 Outlook 创建的样本，修复后 **ANSI 18 项、Unicode 51 项对照全部通过**。50 的研究与选型任务完成；这不代表 51–53 的产品导入已实现，也不承诺任意损坏 PST 可恢复。

## 根因及验证

1. 上游 LTP 将 PtypString8 固定作为 UTF-8；实际样本为 CP936。修改为优先读取消息代码页，再使用显式源代码页。目录没有可用代码页时，本样本明确传入 936，不依赖运行电脑的区域设置。
2. ANSI 主题的堆分配包含零终止符后的填充。直接解码整个分配产生尾部乱码；仅改代码页会触发这些填充字节的解码错误。补充终止符边界后主题及 HTML 恢复。PC 和 TC 同时处理主题前缀，补充修复 Unicode 嵌入邮件前缀。
3. ANSI 普通邮件属性中确实不存在 `PidTagBody`，正文保存在 `PidTagRtfCompressed`。CRC 检查后使用 MimeKit.Core 5.0.0 RTF 转换器恢复中文，保留原 RTF 和 `textSource`。最后一个结构性段落换行被移除，额外空行及尾部空格有回归保护。
4. 修复上游四处短读警告涉及的读取路径，其中 RTF 流重载还存在少分配一个字节的问题。探测工具构建现在为 0 警告、0 错误。

最小复现及回归为 `--self-test` 与 `python scripts/check-pst-fixtures.py`。已实际观察原版三项失败、只改代码页时填充解码失败、修复边界后仅正文失败、转换 RTF 后全部通过。加强检查覆盖中文目录、嵌入主题/正文、正文精确相等、HTML 实体解码后精确相等、CID、附件逐字节哈希及非邮件识别。HTML 字符实体与字面中文等价，不将其当成正文丢失。

## 损坏与规模

- 对现有 ANSI **副本**中的 `MMB-rtf` 压缩正文仅破坏 CRC 字节，原文件不写入。解析返回 2，报告该邮件 CRC 错误，另外 3 封仍可读，包括其后的嵌入邮件。
- 512 字节截断副本被明确拒绝，无挂起。
- Unicode 原样本 143,754,240 字节、37 项、35 个附件；本轮约 1.03 秒、峰值工作集约 50.5 MiB。ANSI 271,360 字节约 0.16 秒、42.6 MiB。性能为本机热缓存对照，未验证多 GiB、冷盘、加密/受保护邮件或所有 PST 变体。
- 两个原 PST 及读取副本 SHA-256 与前次一致；新损坏副本也在读取前后保持不变。未启动 Outlook、未连接邮箱，没有重建样本。

证据：`artifacts/outlook-acceptance/ansi-fixed-checks.log`、`ansi-build.log`，及 `final/comparison.json`、`corrupt-item-report.json`。原始哈希见[前次报告](2026-11-17-protocol-outlook.md)。

## 路线与分发条件

选定技术候选为 **固定 XstReader 源码 + 本地可审阅补丁 + MimeKit.Core 5.0.0**，适用于本轮验证范围。纯托管、Windows x64、.NET 10 JIT，框架依赖；不需要本机 Outlook 才能读取。与 OOM 路线比较：OOM 对本机 Outlook 安装、位数和 Profile 有依赖，本路径可离线处理独立 PST。商业库本轮未购买，也无需因此阻塞当前样本修复。

必须保留的限制：缺少代码页时要让用户明确指定；损坏项目报告并隔离，不能静默跳过后宣称完整；多 GiB 文件需要后续产品级压力验收。`Crypto.cs` 文件头另注明 libpff LGPLv3+ 来源，虽根许可证写 Ms-PL，**产品分发前须厘清并落实该部分许可条件或替换实现**，不能只按 Ms-PL 分发。当前工具不进入产品安装包。各修订及许可说明见[补丁目录](../../tools/MyMailBox.PstProbe/Patched/README.md)。

参考：[消息代码页定义](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxprops/9199035b-d1c6-4404-9418-c971ead604c2)、[PST 消息正文属性](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/3b890c8d-0276-491e-be58-ea766c888ab0)、[MimeKit 5.0.0](https://www.nuget.org/packages/MimeKit/5.0.0)、[固定 Crypto 源文件声明](https://github.com/Dijji/XstReader/blob/3b7856c8b3d01bdf8aa13744e476d1ef7761b832/XstReader.Base/Crypto.cs)。
