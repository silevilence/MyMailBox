# Actions 触发与即时失败修复

2026-11-17（Asia/Shanghai），用户报告普通推送错误触发构建并立即失败。

## 根因与修复

- `build.yml` 原先声明了全分支 `push` 及 `pull_request`。现仅允许 `workflow_dispatch` 和 `workflow_call`，普通推送/PR 不构建打包，正式发布仍由 `release.yml` 的 `V*`/`v*` Tag 触发后调用。
- [失败运行](https://github.com/silevilence/MyMailBox/actions/runs/44036203901) 没有创建 job，GitHub 注解为 `(Line: 30, Col: 23): Unrecognized named-value: 'runner'`。`runner.temp` 被用于不允许该上下文的 job 级 `env`。现将测试工作空间变量移至实际构建 step 的 `env`。
- 普通 YAML 解析只能确认语法，不能确认 Actions 上下文合法性。复用本机 Go 模块缓存中的 actionlint 1.7.12 编译工具（未重新下载），修改前复现相同错误，修改后同时校验 build/release 两个工作流通过。

## 真实验证

实现提交：`2b9188f9787c9279f2f5a168748e3ba1dc4483c8`。推送后查询该提交的 push 运行结果为空。随后显式手动触发 [运行 44037700219](https://github.com/silevilence/MyMailBox/actions/runs/44037700219)，事件 `workflow_dispatch`，最终 `success`。

Windows runner 已完成 SDK/依赖准备、完整非 UI 检查、框架依赖启动与缺失运行时指引检查、版本和发布状态机夹具、真实 NSIS 编译，以及构建 artifact 上传。发布夹具输出不是公开 Release，本次没有创建 Tag 或 Release，也未测试正式签名 Secret。

上传 artifact 名称 `MyMailBox-win-x64-2b9188f9787c9279f2f5a168748e3ba1dc4483c8`，平台大小 29,998,640 字节，摘要 `sha256:6022f7f286d0f34d83bb27e027ca6561d21b0b79037e46ba45aa64a5afe4edd2`。依据平台清单及上传日志核验，没有为验证再次下载产物。本地日志留在忽略目录 `artifacts/ci-acceptance/`。NSIS 和 NuGet 使用工作流缓存；本次首次运行产生缓存，后续按缓存键复用。

仍保持 Graph/Exchange 等真实账户验收边界，不把 CI 中的本地替身认作真实邮箱收发成功。参考 [GitHub 上下文可用范围](https://docs.github.com/en/actions/reference/workflows-and-actions/contexts)。
