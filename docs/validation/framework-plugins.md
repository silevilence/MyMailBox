# 桌面与插件验收记录

环境：Windows x64，SDK 10.0.301，Runtime 10.0.12。依赖采用本机 NuGet 缓存；没有重新下载 SDK、运行时或安装包。

## 02 桌面入口

- `dotnet build MyMailBox.slnx`：通过，0 警告、0 错误。
- `dotnet run --project tests/MyMailBox.Checks`：欢迎页经 CordiSharp 激活、释放后注册归零。
- `scripts/publish.ps1`：生成 win-x64 框架依赖产物；运行时配置引用 Microsoft.NETCore.App，无 coreclr.dll。
- 实际窗口启动/关闭、缺少运行时的启动提示在本轮全部实现后的统一验收中记录。
