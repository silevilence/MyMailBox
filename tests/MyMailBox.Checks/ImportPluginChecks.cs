using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static class ImportPluginChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "file-import");
        var store = new SqliteMailStore(directory);
        var parser = new PausingParser();
        var local = new LocalMailboxService(store, new AccountService(store), parser);
        var target = await local.Create(new(null, "导入目标", "local"));
        var pages = new Contributions();
        await using var runtime = new PluginRuntime(pages);
        var definition = PluginChecks.Example();
        definition = definition with { Manifest = definition.Manifest with { Entry = "eml-import", Permissions = ["files.read","mail.write"] } };
        var options = new PluginOptions { InstanceId = "sample-reader", Grants = ["files.read", "mail.write:" + target.Id] };
        await runtime.Start(definition, options);
        var service = new PstImportService(store, local, parser, () => pages.ImportReaders);
        var format = service.Formats.Single(f => f.Id == "sample-reader/eml");
        PstImportChecks.Require(format.Parameters.Single().Id == "folder", "Missing contributed schema");
        var file = Path.Combine(directory, "source.eml");
        var raw = MailChecks.Sample("从外部导入");
        await File.WriteAllBytesAsync(file, raw);
        var preview = await service.PreviewFile(format.Id, file, new() { ["folder"] = "选定目录" }, default);
        PstImportChecks.Require(preview.Folders.Single() is { Path: "选定目录", Items: 1 }, "External preview");
        var import = service.Import(preview, target.Id, null, default);
        await parser.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.Stop(options.InstanceId, cancel: true);
        try { await import; throw new Exception("Removed reader completed a pending item"); }
        catch (OperationCanceledException) { }
        var pending = (await service.Pending(default)).Single();
        PstImportChecks.Require(pending.Format == format.Id && pending.Parameters["folder"] == "选定目录" && pending.Committed == 0, "Source session lost after unload");
        PstImportChecks.Require(!service.Formats.Any(f => f.Id == format.Id), "Removed reader still visible");
        await runtime.Start(definition, options);
        parser.Paused = false;
        var result = await service.Import(preview, target.Id, null, default);
        PstImportChecks.Require(result.Counts.Imported == 1 && File.ReadAllText(result.Report).Contains("EML"), "Missing item result");
        var folder = (await local.Folders(target.Id)).Single(f => f.Name == "选定目录");
        var message = (await store.List(folder.Id, 0, 10)).Items.Single();
        PstImportChecks.Require((await store.Read(message.Id)).SequenceEqual(raw), "Changed original EML");
        PstImportChecks.Require((await service.Import(preview, target.Id, null, default)).Counts.Duplicates == 1, "Replay duplicated external item");
        await File.WriteAllBytesAsync(file, []);
        await PluginChecks.Reject(() => service.Import(preview, target.Id, null, default), "变化");
        PstImportChecks.Require((await store.Read(message.Id)).SequenceEqual(raw), "Stale selection changed an imported message");
        var damaged = await service.PreviewFile(format.Id, file, preview.Parameters, default);
        var failed = await service.Import(damaged, target.Id, null, default);
        PstImportChecks.Require(failed.Counts is { Failed: 1, Imported: 0 } && (await service.Pending(default)).Single().Committed == 0,
            "Unreadable MIME must be reported per item and remain available for retry");
        await runtime.Stop(options.InstanceId);
        Console.WriteLine("PASS external import plugin: shared wizard schema, granted target, cancellation on unload, retained session, repeat without duplicates, exact MIME and item report");
    }

    private sealed class PausingParser : IMimeReader
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Paused = true;
        public async Task<ParsedMail> Parse(byte[] raw, CancellationToken cancellation = default, string? charsetOverride = null)
        {
            Started.TrySetResult();
            if (Paused) await Task.Delay(Timeout.Infinite, cancellation);
            return await new MimeReader().Parse(raw, cancellation, charsetOverride);
        }
    }
}
