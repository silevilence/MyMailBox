using System.Text;
using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class MetadataExporter(IMailStore store, IMimeReader parser, bool json) : IMessageExporter
{
    public static readonly string[] Fields = ["subject", "date", "from", "to", "cc", "size", "attachments"];
    public string FormatId => json ? "json" : "csv";
    public string Name => json ? "JSON 元数据（自选字段）" : "CSV 元数据（自选字段）";
    public async Task<ExportReport> Export(ExportRequest request, IProgress<ExportItemResult>? progress = null, CancellationToken cancellation = default)
    {
        var fields = request.Options.MetadataFields.Distinct().ToArray();
        if (fields.Any(f => !Fields.Contains(f))) throw new ArgumentException("包含不支持的元数据字段。");
        var rows = new List<Dictionary<string, string>>(); var results = new List<ExportItemResult>();
        var directory = Path.GetFullPath(request.Directory); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"邮件元数据-{Guid.NewGuid():N}.{FormatId}");
        foreach (var item in request.Items.DistinctBy(i => i.MessageId))
        {
            ExportItemResult result;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var raw = await store.Read(item.MessageId, cancellation); var mail = await parser.Parse(raw, cancellation);
                var row = new Dictionary<string, string> { ["id"] = item.MessageId.ToString() };
                foreach (var field in fields) row[field] = field switch
                {
                    "subject" => mail.Subject, "date" => mail.Date.ToString("O"), "from" => mail.From, "to" => mail.To, "cc" => mail.Cc,
                    "size" => raw.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "attachments" => string.Join("; ", (mail.Parts ?? []).Where(p => p.Attachment).Select(p => p.Name)), _ => ""
                };
                rows.Add(row); result = new(item.MessageId, "success", path, "元数据已提取，等待写入结果文件。");
            }
            catch (OperationCanceledException) { result = new(item.MessageId, "canceled", null, "已取消。"); }
            catch (Exception error) { result = new(item.MessageId, "failed", null, error.Message); }
            results.Add(result); progress?.Report(result);
        }
        // Keep successful items even when the user cancels the remaining work.
        if (rows.Count > 0)
        {
            var temporary = path + ".partial";
            try
            {
                var text = json ? JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true })
                    : string.Join("\r\n", new[] { string.Join(",", new[] { "id" }.Concat(fields).Select(Csv)) }
                        .Concat(rows.Select(row => string.Join(",", row.Values.Select(Csv))))) + "\r\n";
                await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(!json)); File.Move(temporary, path);
                for (var i = 0; i < results.Count; i++) if (results[i].Status == "success") results[i] = results[i] with { Detail = "元数据已写入。" };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                for (var i = 0; i < results.Count; i++) if (results[i].Status == "success") results[i] = results[i] with { Status = "failed", Path = null, Detail = error.Message };
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new(results.ToArray());
    }
    public static string Csv(string value)
    {
        // Spreadsheet applications may interpret formulas after leading whitespace.
        if (value.Length > 0 && ("=+-@".Contains(value.TrimStart().FirstOrDefault()) || value[0] is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
