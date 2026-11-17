using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class EmlExporter(IMailStore store) : IMessageExporter
{
    public string FormatId => "eml";
    public string Name => "原始 EML（包含完整 MIME 附件）";
    public async Task<ExportReport> Export(ExportRequest request, IProgress<ExportItemResult>? progress = null, CancellationToken cancellation = default)
    {
        var directory = Path.GetFullPath(request.Directory); Directory.CreateDirectory(directory); var results = new List<ExportItemResult>();
        foreach (var item in request.Items.DistinctBy(i => i.MessageId))
        {
            string? temporary = null; ExportItemResult result;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var raw = await store.Read(item.MessageId, cancellation);
                if (raw.Length == 0) throw new InvalidDataException("原文未下载或不可用，请先获取完整邮件。");
                var name = SafeStem(item.Subject) + "-" + item.MessageId.ToString("N");
                var path = Path.Combine(directory, name + ".eml");
                if (File.Exists(path))
                {
                    await using var existing = File.OpenRead(path);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(existing, cancellation)) == Convert.ToHexString(SHA256.HashData(raw)))
                    {
                        result = new(item.MessageId, "existing", path, "已有相同原文，已复用。"); results.Add(result); progress?.Report(result); continue;
                    }
                    var suffix = 2; do { path = Path.Combine(directory, name + "-" + suffix++ + ".eml"); } while (File.Exists(path));
                }
                temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await output.WriteAsync(raw, cancellation); await output.FlushAsync(cancellation);
                }
                cancellation.ThrowIfCancellationRequested(); File.Move(temporary, path); temporary = null;
                result = new(item.MessageId, "success", path, "原文已完整导出。");
            }
            catch (OperationCanceledException) { result = new(item.MessageId, "canceled", null, "已取消；可重新导出，已完成文件会复用。"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            { result = new(item.MessageId, "failed", null, error.Message); }
            finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            results.Add(result); progress?.Report(result);
        }
        return new(results.ToArray());
    }
    public static string SafeStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).Take(60).ToArray()).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "邮件";
        if (Regex.IsMatch(safe, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) safe = "邮件-" + safe;
        return safe;
    }
}
