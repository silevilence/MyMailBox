# CordiSharp 适配边界

固定 NuGet **CordiSharp 0.2.0**（MIT），包内 repository commit 为 `7b6cde4f43013ae4ff026b5f7bea5e5f9dd9300f`。复用本机包缓存，不复制修改其他项目实现。

上游来源：[项目](https://github.com/oniemiddle/CordiSharp)、[固定版本加载器](https://github.com/oniemiddle/CordiSharp/blob/7b6cde4f43013ae4ff026b5f7bea5e5f9dd9300f/src/CordiSharp/Loading/AssemblyLoaderService.cs)。

- Context/Fiber 提供服务、事件及资源生命周期；AssemblyLoaderService 负责可回收程序集加载、插件发现和验证卸载。
- 适配层负责 SDK 范围、实例与两类配置、依赖顺序、操作排空、UI 撤销和失败恢复。不调用 HotSwap 假定事务回滚。
- 每实例拥有独立 CordiSharp 根上下文；宿主契约从默认上下文共享，插件独有依赖由插件目录解析。上游会优先解析默认上下文已有程序集；因此不能承诺与宿主同名的私有程序集版本隔离，包安装器必须拒绝此类冲突。
- 同进程插件是可信代码，不是安全沙箱。权限校验约束宿主公开操作入口，不能限制插件直接使用 .NET 文件/网络 API。
- 示例 `ExamplePlugin` 使用共享契约、独有 `PrivateGreeting`、事件和计时器。定向检查 30 轮装入/调用/停用，强制 GC 卸载验证与独占 DLL 文件打开均通过。
- 实际 UI 验收已完成：宿主渲染页面/选择器 30 轮导航与卸载通过；原生动画控件存在 Avalonia 类型引用残留，明确报告失败并要求重启。详见 [统一验收记录](validation/framework-plugins.md)。
