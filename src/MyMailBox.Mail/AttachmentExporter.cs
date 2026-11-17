using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class AttachmentExporter(IMailStore store, IMimeReader parser, IAttachmentService attachments) : IMessageExporter
{
    public string FormatId => "attachments";
    public string Name => "附件（按扩展名筛选）";
    public async Task<ExportReport> Export(ExportRequest request, IProgress<ExportItemResult>? progress = null, CancellationToken cancellation = default)
    {
        var directory = Path.GetFullPath(request.Directory); Directory.CreateDirectory(directory);
        var results = new List<ExportItemResult>();
        var extensions = request.Options.AttachmentExtensions.Select(e => "." + e.Trim().TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Items.DistinctBy(i => i.MessageId))
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var mail = await parser.Parse(await store.Read(item.MessageId, cancellation), cancellation);
                var parts = (mail.Parts ?? []).Where(p => (p.Attachment || request.Options.IncludeInline && p.ContentId is not null) &&
                    (extensions.Count == 0 || extensions.Contains(Path.GetExtension(p.Name)))).ToArray();
                if (parts.Length == 0) Report(new(item.MessageId, "skipped", null, "没有符合筛选的附件。"));
                foreach (var part in parts)
                {
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var target = Path.Combine(directory, item.MessageId.ToString("N"));
                        var saved = await attachments.Save(item.MessageId, part.Path, target, cancellation: cancellation);
                        var name = request.Options.AttachmentNameTemplate.Replace("{subject}", mail.Subject).Replace("{name}", Path.GetFileNameWithoutExtension(part.Name)).Replace("{id}", item.MessageId.ToString("N"));
                        var extension = new string(Path.GetExtension(saved.Path).Where(c => char.IsAsciiLetterOrDigit(c) || c == '.').Take(20).ToArray());
                        var final = Path.Combine(target, EmlExporter.SafeStem(name) + extension);
                        if (!string.Equals(final, saved.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            var stem = Path.GetFileNameWithoutExtension(final); var suffix = 2;
                            while (File.Exists(final) || Directory.Exists(final)) final = Path.Combine(target, stem + "-" + suffix++ + extension);
                            File.Move(saved.Path, final);
                        }
                        Report(new(item.MessageId, "success", final, "附件已导出。") { Part = part.Path });
                    }
                    catch (OperationCanceledException) { Report(new(item.MessageId, "canceled", null, "附件导出已取消。") { Part = part.Path }); }
                    catch (Exception error) { Report(new(item.MessageId, "failed", null, error.Message) { Part = part.Path }); }
                }
            }
            catch (OperationCanceledException) { Report(new(item.MessageId, "canceled", null, "已取消。")); }
            catch (Exception error) { Report(new(item.MessageId, "failed", null, "原文不可用或附件解析失败：" + error.Message)); }
        }
        return new(results.ToArray());
        void Report(ExportItemResult result) { results.Add(result); progress?.Report(result); }
    }
}
