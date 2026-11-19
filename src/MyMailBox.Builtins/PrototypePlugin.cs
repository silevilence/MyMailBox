using CordiSharp;
using MyMailBox.Contracts;

namespace MyMailBox.Builtins;

/// <summary>Explicitly simulated flows; no network, real message send, or deletion.</summary>
public static class PrototypePlugin
{
    public static readonly Choice[] Accounts =
    [new("work", "工作 · alex@work.example · IMAP/SMTP · /收件箱"),
     new("personal", "个人 · alex@personal.example · IMAP/SMTP · /收件箱"),
     new("local", "本地归档 · 本地邮箱 · /年度归档")];

    public static void Apply(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        foreach (var page in CreatePages()) context.Effect(() => host.Add(page with { DevelopmentOnly = true }));
    }

    public static IReadOnlyList<Page> CreatePages()
    {
        var flow = new PrototypeFlow();
        return
        [new("read", "阅读原型", "演示数据 · 账户 → 目录 → 邮件。远程内容默认不加载。", [new("read-account", "账户和目录", "work", Accounts)],
            [new("read-message", "打开邮件"), new("read-empty", "演示空目录"), new("read-error", "演示加载失败"), new("read-retry", "重试加载")], async (values, action) =>
            {
                await Task.Delay(120);
                var account = Accounts.Single(p => p.Id == values["read-account"]).Label;
                return action switch
                {
                    "read-empty" => account + "\n此目录没有邮件。你可以切换账户或返回收件箱。",
                    "read-error" => account + "\n加载失败：模拟离线。已保留当前选择，请点击“重试加载”。",
                    _ => account + "\n主题：本周安排\n发件人：team@example.test\n正文：请在周五前确认会议时间。\n外部图片未加载。"
                };
            }),
         new("compose", "写信原型", "演示数据 · 先确认发送身份。此原型只保存本次会话的模拟草稿，不发送真实邮件。",
            [new("send-account", "发送身份", "work", Accounts.Where(p => p.Id != "local").ToArray()), new("recipient", "收件人", "team@example.test"),
             new("subject", "主题", "会议时间确认"), new("body", "正文", "周五上午可以参加会议。", Multiline: true)],
            [new("preview-draft", "预览发送身份"), new("save-draft", "保存模拟草稿"), new("restore-draft", "恢复模拟草稿")], (values, action) =>
            {
                if (action == "restore-draft") return Task.FromResult(flow.Draft ?? "尚无草稿。填写正文后保存模拟草稿。");
                if (string.IsNullOrWhiteSpace(values["recipient"]) || !values["recipient"].Contains('@')) return Task.FromResult("收件人：请输入包含 @ 的地址，填写内容已保留。");
                var draft = $"发送身份：{Accounts.Single(p => p.Id == values["send-account"]).Label}\n收件人：{values["recipient"]}\n主题：{values["subject"]}\n{values["body"]}";
                if (action == "save-draft") flow.Draft = draft;
                return Task.FromResult((action == "save-draft" ? "模拟草稿已保存\n" : "发送前预览（不会发送）\n") + draft);
            }),
         new("automation", "自动化原型", "演示数据 · 触发条件与实际处理的邮件分开选择。先预览来源和目标，再演示执行。",
            [new("trigger-account", "判定阈值的账户", "work", Accounts[..2]), new("threshold", "邮件数量大于", "1000"),
             new("target-account", "实际处理的账户", "personal", Accounts[..2]), new("age-days", "选取早于多少天的邮件", "30"),
             new("destination", "归档目标", "local", [Accounts[2]])],
            [new("preview-plan", "预览范围"), new("run-plan", "演示归档"), new("fail-plan", "演示中断")], (values, action) => Task.FromResult(flow.Automation(values, action))),
         new("recovery", "恢复原型", "演示数据 · 失败时保留来源，从尚未完成的步骤继续。重复恢复不会重复产生副本。", [],
            [new("show-history", "查看执行记录"), new("resume-plan", "从失败步骤继续")], (_, action) => Task.FromResult(action == "resume-plan" ? flow.Resume() : flow.History))];
    }
}

public sealed class PrototypeFlow
{
    public string? Draft { get; set; }
    public string History { get; private set; } = "还没有执行记录。请先在自动化原型中预览范围。";
    private string? _preview;
    private string? _fingerprint;
    private bool _interrupted;
    private bool _completed;
    public string Automation(IReadOnlyDictionary<string, string> values, string action)
    {
        if (!int.TryParse(values["threshold"], out var threshold) || threshold < 0) return "邮件数量阈值：请输入非负整数。";
        if (!int.TryParse(values["age-days"], out var days) || days < 1) return "邮件年龄：请输入大于 0 的天数。";
        var fingerprint = string.Join("|", values.OrderBy(p => p.Key).Select(p => p.Value));
        if (action == "preview-plan")
        {
            _completed = false; _interrupted = false; _fingerprint = fingerprint;
            var trigger = values["trigger-account"] == "work" ? "工作" : "个人";
            var source = values["target-account"] == "work" ? "工作" : "个人";
            var count = values["trigger-account"] == "work" ? 1200 : 600;
            _preview = count > threshold ? $"条件：{trigger} /收件箱，{count} > {threshold}\n处理来源：{source} /收件箱，早于 {days} 天\n归档目标：本地归档 /年度归档\n将处理 3 封模拟邮件；先复制并校验，再清理来源。" : null;
            return _preview ?? "当前阈值未满足，没有选取或修改邮件。";
        }
        if (_preview is null || _fingerprint != fingerprint) return "选择已变化或尚未预览，请先预览范围。";
        if (_completed) return History;
        _interrupted = action == "fail-plan";
        _completed = !_interrupted;
        return History = _preview + (_interrupted
            ? "\n模拟中断：已完成 2/3 封复制，来源全部保留。请前往恢复原型继续。"
            : "\n演示完成：3/3 封已归档，来源清理已确认。");
    }
    public string Resume()
    {
        if (!_interrupted) return History;
        _interrupted = false; _completed = true;
        return History = _preview + "\n恢复完成：仅继续第 3 封，3/3 封已归档；没有重复复制。";
    }
}
