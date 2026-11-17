using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class ExportChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-export-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(Path.Combine(directory, "mail")); var raw = MimeChecks.Multipart(); var parser = new MimeReader(); var metadata = await parser.Parse(raw);
        var id = await store.Import(SqliteMailStore.Inbox, raw, metadata); var second = await store.Import(SqliteMailStore.Inbox, raw, metadata);
        IMessageExporter exporter = new EmlExporter(store); var output = Path.Combine(directory, "中文导出");
        var report = await exporter.Export(new([new(id, "../../中文:邮件?"), new(Guid.NewGuid(), "缺失"), new(second, "CON")], output));
        MailChecks.Check(report.Items.Select(i => i.Status).SequenceEqual(new[] { "success", "failed", "success" }), "Partial results incorrect");
        foreach (var result in report.Items.Where(i => i.Status == "success"))
        {
            MailChecks.Check(Path.GetDirectoryName(result.Path!) == output, "Export escaped output folder");
            var exported = await File.ReadAllBytesAsync(result.Path!); MailChecks.Check(exported.SequenceEqual(raw), "MIME/attachment fidelity changed");
            var reimported = await store.Import(SqliteMailStore.Inbox, exported, await parser.Parse(exported)); MailChecks.Check((await store.Read(reimported)).SequenceEqual(raw), "Reimport failed");
        }
        var repeated = await exporter.Export(new([new(id, "../../中文:邮件?")], output)); MailChecks.Check(repeated.Items.Single().Status == "existing", "Repeat did not resume");
        await File.WriteAllTextAsync(report.Items[0].Path!, "keep this unrelated file");
        var conflict = await exporter.Export(new([new(id, "../../中文:邮件?")], output));
        MailChecks.Check(conflict.Items[0].Path != report.Items[0].Path && File.ReadAllText(report.Items[0].Path!) == "keep this unrelated file", "Conflict overwrote existing file");
        using var cancellation = new CancellationTokenSource();
        var canceled = await exporter.Export(new([new(id, "取消1"), new(second, "取消2")], output), new Callback(_ => cancellation.Cancel()), cancellation.Token);
        MailChecks.Check(canceled.Items[0].Status == "success" && canceled.Items[1].Status == "canceled", "Cancel did not preserve per-item result");
        var badStem = "无法替换"; var blockedPath = Path.Combine(output, EmlExporter.SafeStem(badStem) + "-" + id.ToString("N") + ".eml"); Directory.CreateDirectory(blockedPath);
        var blocked = await exporter.Export(new([new(id, badStem)], output));
        MailChecks.Check(blocked.Items[0].Status == "failed" && !Directory.EnumerateFiles(output, "*.partial-*").Any(), "Write failure left a complete-looking file");
        var longDirectory = Path.Combine(output, new string('a', 100), new string('b', 100));
        var longResult = await exporter.Export(new([new(id, new string('中', 200))], longDirectory));
        MailChecks.Check(longResult.Items.Single().Status == "success", "Long path export failed");
        Console.WriteLine("PASS EML export: byte-identical attachments/reimport, Chinese/unsafe/reserved/long names, conflicts, repeated resume, partial failure, cancel, temporary cleanup");
    }
    private sealed class Callback(Action<ExportItemResult> callback) : IProgress<ExportItemResult> { public void Report(ExportItemResult value) => callback(value); }
}
