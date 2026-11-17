using System.Text.Json;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static class BulkExportChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-bulk-export-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(Path.Combine(directory, "mail")); var parser = new MimeReader();
        var raw = MimeChecks.Multipart(); var mail = await parser.Parse(raw);
        var first = await store.Import(SqliteMailStore.Inbox, raw, mail with { Subject = "scope selected" });
        var account = Guid.NewGuid(); var folder = Guid.NewGuid();
        using (var connection = store.Open())
        {
            using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO folders(id,account_id,name,path,role) VALUES($id,$account,'Second','/Second','custom')";
            command.Parameters.AddWithValue("$id", folder.ToString()); command.Parameters.AddWithValue("$account", account.ToString()); command.ExecuteNonQuery();
        }
        var second = await store.Import(folder, raw, mail with { Subject = "scope selected" });
        await store.Import(folder, raw, mail with { Subject = "not selected" });
        var scope = await ExportScope.Collect(store, [account], [SqliteMailStore.Inbox, folder], "scope");
        MailChecks.Check(scope.Length == 2 && scope.Select(i => i.MessageId).ToHashSet().SetEquals([first, second]), "Account/folder scope, subject filter or dedup failed");
        var output = Path.Combine(directory, "output"); var request = new ExportRequest(scope, output);
        var json = await new MetadataExporter(store, parser, true).Export(request);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(json.Items[0].Path!)))
        {
            MailChecks.Check(document.RootElement.GetArrayLength() == 2, "JSON scope mismatch");
            MailChecks.Check(!document.RootElement[0].TryGetProperty("from", out _) && !document.RootElement[0].TryGetProperty("cc", out _), "Sensitive fields defaulted on");
        }
        var csv = await new MetadataExporter(store, parser, false).Export(request with { Options = new() { MetadataFields = ["subject", "from", "cc"] } });
        var csvText = await File.ReadAllTextAsync(csv.Items[0].Path!); MailChecks.Check(csvText.StartsWith("\"id\",\"subject\",\"from\",\"cc\""), "CSV columns wrong");
        foreach (var formula in new[] { "=1+1", "+cmd", "-cmd", "@SUM(1)", "  =2", "\t=3", "\r=4" }) MailChecks.Check(MetadataExporter.Csv(formula).StartsWith("\"'"), "CSV formula not neutralized");
        MailChecks.Check(MetadataExporter.Csv("a,\"b\"") == "\"a,\"\"b\"\"\"", "CSV quote escaping failed");
        var service = new AttachmentService(store); var exporter = new AttachmentExporter(store, parser, service);
        var parts = mail.Parts!.Where(p => p.Attachment).ToArray(); MailChecks.Check(parts.Length > 0, "Fixture lacks attachment");
        var extension = Path.GetExtension(parts[0].Name);
        var attachments = await exporter.Export(request with { Options = new() { AttachmentExtensions = [extension], AttachmentNameTemplate = "../../CON:{name}" } });
        MailChecks.Check(attachments.Items.Any(r => r.Status == "success") && attachments.Items.All(r => r.Status != "failed"), "Attachment export failed");
        foreach (var result in attachments.Items.Where(r => r.Status == "success"))
            MailChecks.Check(Path.GetDirectoryName(result.Path!) == Path.Combine(output, result.MessageId.ToString("N")) && File.Exists(result.Path), "Template escaped output directory");
        var repeated = await exporter.Export(request with { Options = new() { AttachmentExtensions = [extension], AttachmentNameTemplate = "../../CON:{name}" } });
        MailChecks.Check(!repeated.Items.Where(r => r.Status == "success").Select(r => r.Path).Intersect(attachments.Items.Select(r => r.Path)).Any(), "Duplicate overwrote attachment");
        var filtered = await exporter.Export(request with { Options = new() { AttachmentExtensions = ["does-not-exist"] } });
        MailChecks.Check(filtered.Items.All(r => r.Status == "skipped"), "Extension filter ignored");
        var missing = await exporter.Export(new([new(Guid.NewGuid(), "missing")], output)); MailChecks.Check(missing.Items.Single().Status == "failed", "Missing raw silently skipped");
        var failed = await new AttachmentExporter(store, parser, new FailingAttachments()).Export(request);
        MailChecks.Check(failed.Items.All(r => r.Status == "failed" && r.Part is not null), "Attachment failures not reported individually");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var report = await exporter.Export(request, cancellation: canceled.Token); MailChecks.Check(report.Items.Length == 2 && report.Items.All(r => r.Status == "canceled"), "Cancellation report incomplete");
        using var partialCancellation = new CancellationTokenSource();
        var partial = await new MetadataExporter(store, parser, true).Export(request, new Callback(_ => partialCancellation.Cancel()), partialCancellation.Token);
        MailChecks.Check(partial.Items[0].Status == "success" && partial.Items[1].Status == "canceled" && File.Exists(partial.Items[0].Path), "Metadata cancellation lost completed rows");
        var pages = new Contributions(); await using var runtime = new PluginRuntime(pages);
        var definition = PluginChecks.Example() with { Manifest = PluginChecks.Example().Manifest with { Entry = "exporter" } };
        await runtime.Start(definition, new() { InstanceId = "external-export" });
        var external = pages.Exporters.Single(); var externalResult = await external.Export(request);
        MailChecks.Check(externalResult.Items.Length == scope.Length && File.ReadAllLines(externalResult.Items[0].Path!).Length == scope.Length, "External format contract failed");
        await runtime.Stop("external-export"); MailChecks.Check(pages.Exporters.Count == 0, "External exporter leaked after stop");
        try { await external.Export(request); throw new Exception("Stopped exporter accepted work"); } catch (InvalidOperationException) { }
        MailChecks.Check(typeof(Page).GetConstructors().Any(c => c.GetParameters().Length == 6), "Original Page constructor ABI missing");
        Console.WriteLine("PASS bulk export: multi-account scope/filter/dedup, sensitive fields, CSV injection, JSON, attachments/template/conflicts/failures/cancel, external plugin registration/stop");
    }
    private sealed class Callback(Action<ExportItemResult> callback) : IProgress<ExportItemResult> { public void Report(ExportItemResult value) => callback(value); }
    private sealed class FailingAttachments : IAttachmentService
    {
        public Task<SavedAttachment> Save(Guid message, string part, string directory, IProgress<long>? progress = null, CancellationToken cancellation = default) => throw new IOException("download unavailable");
        public Task<AttachmentPreview> Preview(Guid message, string part, CancellationToken cancellation = default) => throw new NotSupportedException();
    }
}
