using System.Text;
using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class AttachmentService(IMailStore store, IAttachmentPreviewer? previewer = null) : IAttachmentService
{
    public async Task<SavedAttachment> Save(Guid message, string part, string directory, IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        var raw = await store.Read(message, cancellation); using var source = new MemoryStream(raw, false); using var mime = Parse(source, cancellation);
        var entity = Find(mime.Body, part); var original = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
        var name = SafeName(original ?? (entity is MessagePart ? "嵌入邮件.eml" : "附件.bin"));
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory); var path = Path.Combine(directory, name);
        var index = 2; while (File.Exists(path) || Directory.Exists(path)) path = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + " (" + index++ + ")" + Path.GetExtension(name));
        var temporary = path + ".partial-" + Guid.NewGuid().ToString("N"); long bytes;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var output = new CountingStream(file, long.MaxValue, progress))
            { await Decode(entity, output, cancellation); await output.FlushAsync(cancellation); bytes = output.Written; }
            cancellation.ThrowIfCancellationRequested(); File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var canOpen = CanOpenExternally(path);
        return new(path, bytes, canOpen, canOpen ? "已保存。仅在信任来源时选择外部打开。" : "此类型可能执行代码，只提供保存；不会从应用中启动。");
    }
    public async Task<AttachmentPreview> Preview(Guid message, string part, CancellationToken cancellation = default)
    {
        var raw = await store.Read(message, cancellation); using var source = new MemoryStream(raw, false); using var mime = Parse(source, cancellation);
        var entity = Find(mime.Body, part); var renderer = previewer ?? new BasicAttachmentPreviewer();
        if (!renderer.Supports(entity.ContentType.MimeType)) return new("unavailable", "此格式不支持受限预览，可保存后使用合适的应用打开。");
        using var data = new MemoryStream(); await using (var limited = new CountingStream(data, 2 * 1024 * 1024, null)) await Decode(entity, limited, cancellation);
        return await renderer.Preview(entity.ContentType.MimeType, data.ToArray(), cancellation);
    }
    private static MimeMessage Parse(Stream raw, CancellationToken cancellation)
    {
        var options = ParserOptions.Default.Clone(); options.MaxMimeDepth = 32; return MimeMessage.Load(options, raw, true, cancellation);
    }
    public static MimeEntity Find(MimeEntity? root, string path)
    {
        var steps = path.Split('.'); if (root is null || steps.Length > 32 || steps[0] != "1") throw new InvalidDataException("附件引用无效。");
        var entity = root;
        foreach (var step in steps.Skip(1))
        {
            if (!int.TryParse(step, out var index) || index < 1) throw new InvalidDataException("附件引用无效。");
            entity = entity switch { Multipart multipart when index <= multipart.Count => multipart[index - 1], MessagePart nested when index == 1 && nested.Message?.Body is not null => nested.Message.Body, _ => throw new InvalidDataException("附件已不存在。") };
        }
        if (entity is Multipart) throw new InvalidDataException("请选择具体附件，而不是 MIME 容器。");
        return entity;
    }
    private static Task Decode(MimeEntity entity, Stream output, CancellationToken cancellation) => entity switch
    {
        MessagePart nested when nested.Message is not null => nested.Message.WriteToAsync(output, cancellation),
        MimePart part when part.Content is not null => part.Content.DecodeToAsync(output, cancellation),
        _ => throw new InvalidDataException("附件内容缺失，未保存不完整文件。")
    };
    public static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/')); var extension = Path.GetExtension(name);
        if (extension.Length > 12 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
        return EmlExporter.SafeStem(Path.GetFileNameWithoutExtension(name)) + extension;
    }
    public static bool CanOpenExternally(string path) => !new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".lnk", ".url", ".scr", ".msi", ".msp", ".hta", ".dll", ".reg", ".html", ".htm", ".svg", ".iso", ".img", ".docm", ".xlsm", ".pptm" }.Contains(Path.GetExtension(path));

    internal sealed class CountingStream(Stream inner, long maximum, IProgress<long>? progress, string limitMessage = "附件超过 2 MiB 预览限制，请保存后查看。") : Stream
    {
        public long Written { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); Written += count; progress?.Report(Written); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellation) { Check(count); await inner.WriteAsync(buffer.AsMemory(offset, count), cancellation); Written += count; progress?.Report(Written); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default) { Check(buffer.Length); await inner.WriteAsync(buffer, cancellation); Written += buffer.Length; progress?.Report(Written); }
        private void Check(int count) { if (Written + count > maximum) throw new InvalidDataException(limitMessage); }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken cancellation) => inner.FlushAsync(cancellation);
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => Written; public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public sealed class BasicAttachmentPreviewer : IAttachmentPreviewer
{
    public bool Supports(string type) => type is "text/plain" or "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "message/rfc822";
    public async Task<AttachmentPreview> Preview(string type, byte[] data, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (type == "message/rfc822") { var mail = await new MimeReader().Parse(data, cancellation); return new("text", mail.Subject + "\n" + mail.From + "\n\n" + mail.Text); }
        if (type == "text/plain") return new("text", Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(data.Length, 256 * 1024))) + (data.Length > 256 * 1024 ? "\n（预览截断，保存可查看完整内容）" : ""));
        if (Supports(type)) return new("image", "", new("preview", type, data));
        return new("unavailable", "此格式不支持预览。");
    }
}
