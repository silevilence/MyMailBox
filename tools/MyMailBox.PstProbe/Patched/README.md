# XstReader 局部修订

基于 `Dijji/XstReader@3b7856c8b3d01bdf8aa13744e476d1ef7761b832`。本目录保留上游版权及 Ms-PL；其余源码仍由 `scripts/prepare-pst-probe.ps1` 从已校验的本地缓存复用。项目明确排除对应上游文件，编译本目录版本，不直接修改缓存。

本项目修改：

- LTP：String8 使用消息 `0x3FFD` / `0x3FDE` 代码页，缺失时使用明确指定的源代码页；以首个零终止符界定值，排除堆填充；统一 PC/TC 的 ANSI 和 Unicode 主题前缀处理。无元数据且无显式回退时，不猜测非 ASCII 字符串。
- XstFile：增加可选源 ANSI 代码页参数；仍只读访问 PST。
- Map/NDB：完整读取所需字节，短读明确失败。
- RtfDecompressor：流重载修复少分配一个字节及忽略当前位置的缺陷；压缩数据继续执行 CRC 检查。
- 工具层通过 MimeKit.Core **5.0.0** 的 RTF 转文本器恢复只有压缩 RTF 的正文，保留原 RTF，并记录正文来源；只移除最后一个结构性段落换行。

研究工具使用的上游 `Crypto.cs` 文件头注明代码来自 libpff、采用 LGPLv3+。此文件仅供本地探测。产品读取器复用本目录的 Ms-PL 修订，并按微软公开的 MS-PST 规范单独实现解码。发布产物的 `Notices` 目录包含 XstReader 的许可证和来源说明。MimeKit.Core 采用 MIT 许可。详见[产品读取器说明](../../../src/MyMailBox.Pst/NOTICE.txt)。

回归：`dotnet build tools/MyMailBox.PstProbe -r win-x64 --self-contained false`，随后 `python scripts/check-pst-fixtures.py`。复用 `artifacts/outlook-acceptance/final/`，不重新下载或创建 Outlook 样本。
