# MyMailBox

Windows x64 邮件工作空间，Avalonia / .NET 10。当前实现范围见 `.scratch/000-mymailbox/`，此阶段不连接真实邮箱。

## 构建与运行

安装 x64 .NET 10 SDK 后：

```powershell
dotnet build MyMailBox.slnx
dotnet run --project src/MyMailBox.Desktop
dotnet run --project tests/MyMailBox.Checks
./scripts/publish.ps1
```

发布目录为 `artifacts/publish`。双击 `MyMailBox.Desktop.exe`，需要系统安装 x64 .NET 10 Runtime。产物明确为框架依赖发布，无 AOT、裁剪或捆绑运行时。`--runtime-check` 仅验证运行时启动，不打开窗口。

SDK 固定在 global.json，包版本和依赖闭包记录在各项目及 packages.lock.json。此轮构建复用本机 NuGet 缓存中的 Avalonia 12.1.1 和 CordiSharp 0.2.0。
