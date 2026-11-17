using CordiSharp;
using CordiSharp.Registry;
using MyMailBox.Contracts;
using PrivateGreeting;

namespace ExamplePlugin;

[Plugin("example")]
[Inject("mailbox.host")]
public sealed class Example : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        if (options.FailInitialization) throw new InvalidOperationException("示例新版本初始化失败");
        var host = context.Get<IPluginHost>("mailbox.host")!;
#if PLUGIN_V2
        const string revision = "版本2";
#else
        const string revision = "版本1";
#endif
        var received = 0;
        var ticks = 0;
        context.Effect(() => host.Subscribe(_ => Interlocked.Increment(ref received)));
        context.Effect(() => new Timer(_ => Interlocked.Increment(ref ticks), null, 20, 20));
        context.Effect(() => host.Command("ping", _ => Task.FromResult($"{revision} · {Words.Message(options.Greeting)}; events={received}; ticks={Volatile.Read(ref ticks)}")));
        context.Effect(() => host.Command("work", async token => { await Task.Delay(1000, token); host.Notify("work-complete"); return "模拟操作完成，未产生真实邮件副作用。"; }));
        context.Effect(() => host.Add(new Page("example", "外部插件示例", "通过共享契约贡献的页面，代码与私有依赖在可回收上下文中运行。", [],
            [new("ping", "调用插件命令")], (_, _) => Task.FromResult(Words.Message(options.Greeting)))));
    }
}

[Plugin("selector")]
[Inject("mailbox.host")]
public sealed class AlternativeSelector : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.AddSelector(new("accounts", "外部账户选择器", () =>
            [new("local", "本地归档 · 本地邮箱 · /年度归档"), new("personal", "个人 · alex@personal.example · /收件箱"), new("work", "工作 · alex@work.example · /收件箱")])));
        context.Effect(() => host.Add(new Page("selector", "选择器示例", "此页及账户选项来自外部第二实现。稳定 ID 与默认实现一致。",
            [new("selected-account", "账户", "personal", Selector: "accounts")], [new("confirm-selection", "确认选择")],
            (values, _) => Task.FromResult("稳定账户 ID：" + values["selected-account"]))));
    }
}

[Plugin("exporter")]
[Inject("mailbox.host")]
public sealed class ExampleExportPlugin : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.AddExporter(new SubjectListExporter()));
    }
    private sealed class SubjectListExporter : IMessageExporter
    {
        public string FormatId => "example-subject-list";
        public string Name => "外部示例 · 主题列表 JSONL";
        public async Task<ExportReport> Export(ExportRequest request, IProgress<ExportItemResult>? progress = null, CancellationToken cancellation = default)
        {
            var directory = Path.GetFullPath(request.Directory); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "主题列表-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var results = new List<ExportItemResult>();
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            foreach (var item in request.Items.DistinctBy(i => i.MessageId))
            {
                ExportItemResult result;
                if (cancellation.IsCancellationRequested) result = new(item.MessageId, "canceled", null, "已取消。");
                else
                {
                    await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
                        { ["id"] = item.MessageId.ToString(), ["subject"] = item.Subject }));
                    result = new(item.MessageId, "success", path, "外部格式已导出。");
                }
                results.Add(result); progress?.Report(result);
            }
            return new(results.ToArray());
        }
    }
}
