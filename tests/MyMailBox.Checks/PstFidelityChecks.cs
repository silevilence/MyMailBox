using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MimeKit;
using MyMailBox.Mail;

internal static class PstFidelityChecks
{
    private sealed record ExpectedMail(string Pst, string Subject, string Body, string Html);
    private sealed record ExpectedFile(string Name, int Bytes, string Sha256);

    public static async Task Run()
    {
        var root = PstImportChecks.Fixtures;
        var oracle = JsonSerializer.Deserialize<ExpectedMail[]>(await File.ReadAllTextAsync(Path.Combine(root, "outlook-expected.json")))!;
        var attachments = JsonSerializer.Deserialize<ExpectedFile[]>(await File.ReadAllTextAsync(Path.Combine(root, "attachments-expected.json")))!.ToDictionary(f => f.Name);
        var store = new SqliteMailStore(Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "pst-fidelity"));
        var mimeReader = new MyMailBox.Mail.MimeReader();
        var local = new LocalMailboxService(store, new AccountService(store), mimeReader);
        var service = new PstImportService(store, local, mimeReader);
        foreach (var format in new[] { "ansi", "unicode", "corrupt-item" })
        {
            var target = await local.Create(new(null, format, "local"));
            var source = Path.Combine(root, format == "corrupt-item" ? "corrupt-item.pst" : $"read-{format}.pst");
            var preview = await service.Preview(source, 936, default);
            var run = await service.Import(preview, target.Id, null, default);
            var rows = (await File.ReadAllLinesAsync(run.Report)).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            PstImportChecks.Require(run.Counts.Imported == (format == "ansi" ? 4 : format == "unicode" ? 36 : 3), "Import count " + format);
            if (format == "corrupt-item")
            {
                PstImportChecks.Require(run.Counts.Failed == 1 && rows.Single(row => row.GetProperty("status").GetString() == "error").GetProperty("detail").GetString()!.Contains("CRC"), "Damaged item did not report CRC");
                PstImportChecks.Require(rows.Any(row => row.GetProperty("Name").GetString() == "MMB-embedded" && row.GetProperty("status").GetString() == "message"), "Did not continue after damage");
                continue;
            }
            if (format == "unicode")
                PstImportChecks.Require(run.Counts.Skipped == 1 && rows.Any(row => row.GetProperty("detail").GetString()!.Contains("IPM.Contact")), "Unreported non-mail item");
            PstImportChecks.Require(run.Counts.Failed == 0, "Unexpected data loss");
            var checkedFiles = 0;
            foreach (var row in rows.Where(r => r.GetProperty("status").GetString() == "message"))
            {
                var raw = await store.Read(row.GetProperty("id").GetGuid());
                using var mime = MimeMessage.Load(new MemoryStream(raw));
                var sample = oracle.FirstOrDefault(item => item.Pst == format && item.Subject == mime.Subject);
                if (mime.Subject!.StartsWith("MMB-plain") || mime.Subject == "MMB-rtf")
                    PstImportChecks.Require(mime.TextBody?.TrimEnd('\r', '\n') == sample!.Body.TrimEnd('\r', '\n'), "Plain/RTF body " + mime.Subject);
                if (mime.Subject == "MMB-html-CID")
                    PstImportChecks.Require(WebUtility.HtmlDecode(mime.HtmlBody!).TrimEnd() == WebUtility.HtmlDecode(sample!.Html).TrimEnd()
                        && mime.BodyParts.Any(p => p.ContentId == "mmb-pixel"), "HTML / CID");
                if (mime.Subject == "MMB-embedded")
                {
                    var nested = mime.BodyParts.OfType<MessagePart>().Single().Message!;
                    PstImportChecks.Require(nested.Subject == "MMB-plain-中文" && nested.TextBody!.Contains("纯文本正文 Alpha 中文"), "Embedded message");
                }
                if (mime.Subject == "MMB-rtf")
                    PstImportChecks.Require(mime.BodyParts.OfType<MimePart>().Any(p => p.FileName == "正文.rtf"), "Original RTF missing");
                foreach (var part in Parts(mime).Where(p => attachments.ContainsKey(p.FileName ?? "")))
                {
                    var expected = attachments[part.FileName!];
                    using var bytes = new MemoryStream(); part.Content!.DecodeTo(bytes);
                    PstImportChecks.Require(bytes.Length == expected.Bytes && Convert.ToHexString(SHA256.HashData(bytes.ToArray())) == expected.Sha256, "Attachment " + expected.Name);
                    checkedFiles++;
                }
            }
            PstImportChecks.Require(checkedFiles == (format == "unicode" ? 35 : 3), "Expected attachment count");
            using var input = File.OpenRead(source);
            PstImportChecks.Require(Convert.ToHexString(await SHA256.HashDataAsync(input)) == preview.Fingerprint, "Changed source PST");
            Console.WriteLine($"PASS {format} PST: {checkedFiles} attachment byte comparisons, source bodies, HTML/CID, embedded mail, original RTF and loss report");
        }
        Console.WriteLine("PASS damaged PST: isolated CRC failure, subsequent mail readable");
    }

    private static IEnumerable<MimePart> Parts(MimeMessage mail)
    {
        foreach (var entity in mail.BodyParts)
        {
            if (entity is MimePart part) yield return part;
            if (entity is MessagePart { Message: { } nested })
                foreach (var attachment in Parts(nested)) yield return attachment;
        }
    }
}
