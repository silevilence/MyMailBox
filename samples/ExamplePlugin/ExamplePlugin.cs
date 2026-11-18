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

// A typed contribution also exercises the host's unload boundary.
[Plugin("typed")]
[Inject("mailbox.host")]
public sealed class TypedExample : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.Add(new Page("typed", "类型化服务示例", "用于验证服务卸载。", [], [],
            (_, _) => Task.FromResult("ready")) { Updates = new Updates() }));
    }
    private sealed class Updates : IUpdateService
    {
        public Task<UpdateSnapshot> Snapshot(CancellationToken cancellation = default) => Task.FromResult(new UpdateSnapshot("1.0.0", new("test/local"), new(UpdateCheckState.NotChecked, "ready", DateTimeOffset.MinValue)));
        public Task Save(UpdateSettings settings, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<UpdateCheck> Check(bool manual, CancellationToken cancellation = default) => Task.FromResult(new UpdateCheck(UpdateCheckState.Current, "ready", DateTimeOffset.MinValue));
        public Task Skip(string? version, CancellationToken cancellation = default) => Task.CompletedTask;
    }
}

[Plugin("search-case")]
[Inject("mailbox.host")]
public sealed class ExampleCaseSearch : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.AddSearchExtension(new("plugin.example.case.v1", "工单编号", "number", "编号等于", SearchInput.Subject,
            _ => "[CASE-", Extract, Match)));
    }

    private static Task<IReadOnlyList<SearchDerived>> Extract(IReadOnlyList<SearchCandidate> batch,CancellationToken token)
    {
        var rows = new List<SearchDerived>();
        foreach (var item in batch)
        {
            token.ThrowIfCancellationRequested(); int start = item.Text.IndexOf("[CASE-",StringComparison.OrdinalIgnoreCase);
            int end = start < 0 ? -1 : item.Text.IndexOf(']',start + 6);
            rows.Add(new(item.Id, end > start + 6 && uint.TryParse(item.Text.AsSpan(start+6,end-start-6),out var number) ? number.ToString() : null));
        }
        return Task.FromResult<IReadOnlyList<SearchDerived>>(rows);
    }

    private static Task<IReadOnlyList<SearchDecision>> Match(string value,IReadOnlyList<SearchCandidate> batch,CancellationToken token)
    {
        if (!uint.TryParse(value,out var number)) throw new InvalidDataException("工单编号需要非负整数。");
        var rows = new List<SearchDecision>();
        foreach (var item in batch)
        {
            token.ThrowIfCancellationRequested(); bool match = item.Text == number.ToString();
            rows.Add(new(item.Id,match,match ? new SearchExcerpt("工单编号 ",item.Text,"") : null));
        }
        return Task.FromResult<IReadOnlyList<SearchDecision>>(rows);
    }
}

[Plugin("plain-editor")]
[Inject("mailbox.host")]
public sealed class ExamplePlainEditor : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host=context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(()=>host.AddEditor(new("plain","外部纯文本编辑器",false,[new("确认收悉","来信已收到。")],
            body=>body with{Rich=false},(text,token)=>Task.FromResult(text.Contains("teh",StringComparison.OrdinalIgnoreCase)?"建议将 teh 改为 the。":"示例词表未发现问题。")) { Signature=_=>"敬上" }));
    }
}
