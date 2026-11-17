using System.Text;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class AttachmentChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-attachments-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(directory); var parser = new MimeReader(); var raw = MimeChecks.Multipart(); var mail = await parser.Parse(raw); var id = await store.Import(SqliteMailStore.Inbox, raw, mail);
        var service = new AttachmentService(store); var output = Path.Combine(directory, "saved");
        var attachment = mail.Parts!.Single(p => p.Name == "data.bin"); var saved = await service.Save(id, attachment.Path, output);
        MailChecks.Check((await File.ReadAllBytesAsync(saved.Path)).SequenceEqual(new byte[] { 0, 1, 2, 255, 254 }), "Decoded attachment changed");
        var again = await service.Save(id, attachment.Path, output); MailChecks.Check(again.Path != saved.Path && File.Exists(saved.Path), "Duplicate overwrote attachment");
        var nested = mail.Parts!.Single(p => p.ContentType == "message/rfc822");
        MailChecks.Check((await service.Preview(id, nested.Path)).Text.Contains("Nested body"), "Embedded message preview failed");
        var inline = mail.Parts!.Single(p => p.ContentId == "logo"); MailChecks.Check((await service.Preview(id, inline.Path)).Kind == "image", "CID preview unavailable");
        MailChecks.Check((await service.Preview(id, attachment.Path)).Kind == "unavailable", "Unknown attachment executed/previewed");
        foreach (var name in new[] { "../../attack.exe", "..\\..\\CON.exe", "evil:stream.cmd" })
        {
            var safe = AttachmentService.SafeName(name); MailChecks.Check(!safe.Contains('/') && !safe.Contains('\\') && !safe.Contains(':'), "Unsafe path retained");
            MailChecks.Check(!AttachmentService.CanOpenExternally(safe), "Executable allowed to open");
        }
        var replaced = new AttachmentService(store, new AlternativePreviewer());
        MailChecks.Check((await replaced.Preview(id, attachment.Path)).Text == "second preview implementation: 5 bytes", "Previewer replacement failed");
        var largeRaw = Encoding.ASCII.GetBytes("From: sender@example.test\r\nSubject: Large attachment\r\nContent-Type: application/octet-stream; name=large.bin\r\nContent-Disposition: attachment; filename=large.bin\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(new byte[3 * 1024 * 1024]));
        var largeId = await store.Import(SqliteMailStore.Inbox, largeRaw, await parser.Parse(largeRaw));
        using var cancellation = new CancellationTokenSource();
        try { await service.Save(largeId, "1", output, new CancelProgress(cancellation), cancellation.Token); throw new Exception("Save cancellation ignored"); } catch (OperationCanceledException) { }
        MailChecks.Check(!Directory.EnumerateFiles(output).Any(p => p.Contains("partial-") || Path.GetFileName(p) == "large.bin"), "Incomplete attachment left visible");
        MailChecks.Check((await store.Read(largeId)).SequenceEqual(largeRaw), "Canceled save changed source");
        Console.WriteLine("PASS attachments: MIME decode, CID/embedded preview, safe names, collision policy, dangerous-open restriction, replacement previewer, streaming cancel/cleanup/source preservation");
    }
    private sealed class AlternativePreviewer : IAttachmentPreviewer
    {
        public bool Supports(string type) => true;
        public Task<AttachmentPreview> Preview(string type, byte[] data, CancellationToken cancellation = default) => Task.FromResult(new AttachmentPreview("text", $"second preview implementation: {data.Length} bytes"));
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<long> { public void Report(long value) { if (value > 0) cancellation.Cancel(); } }
}
