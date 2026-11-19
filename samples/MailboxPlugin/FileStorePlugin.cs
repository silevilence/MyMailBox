using System.Text.Json;
using System.Text.Json.Serialization;
using CordiSharp;
using CordiSharp.Registry;
using MimeKit;
using MyMailBox.Contracts;

namespace MailboxPlugin;

[Plugin("file-store")]
[Inject("mailbox.host")]
public sealed class FileStorePlugin : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        var workspace = Environment.GetEnvironmentVariable("MYMAILBOX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMailBox");
        var storage = new FileMailStore(Path.Combine(workspace, "plugin-data", host.InstanceId, "eml"));
        context.Effect(() => host.AddMailBackend(storage, new FileMailParser()));
    }
}

internal sealed class FileMailParser : IMimeReader
{
    public async Task<ParsedMail> Parse(byte[] raw, CancellationToken cancellation = default, string? charsetOverride = null)
    {
        using var stream = new MemoryStream(raw, false);
        var options = ParserOptions.Default.Clone();
        if (!string.IsNullOrWhiteSpace(charsetOverride)) options.CharsetEncoding = System.Text.Encoding.GetEncoding(charsetOverride);
        var message = await MimeMessage.LoadAsync(options, stream, cancellation);
        return new(message.Subject ?? "", message.From.ToString(), message.To.ToString(), message.Date,
            message.TextBody ?? "", message.Cc.ToString(), message.HtmlBody ?? "");
    }
}

internal sealed partial class FileMailStore(string directory) : IMailStore
{
    private static readonly MailFolder Folder = new(new("18be7a67-e113-49ca-8ea4-3511ed6c3a86"),
        new("670065f4-4810-434b-a246-547fbd10c565"), "文件邮箱", "文件邮箱", "inbox");
    [JsonSerializable(typeof(MailSummary))]
    private sealed partial class SummaryJson : JsonSerializerContext { }

    public Task<IReadOnlyList<MailFolder>> Folders(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<MailFolder>>([Folder]);
    }

    public async Task<MailPageResult> List(Guid folderId, int offset, int limit, CancellationToken cancellation = default)
    {
        CheckFolder(folderId);
        if (offset < 0 || limit < 1) throw new ArgumentOutOfRangeException();
        Directory.CreateDirectory(directory);
        var summaries = new List<MailSummary>();
        foreach (var path in Directory.EnumerateDirectories(directory))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _)) continue;
            using var file = File.OpenRead(Path.Combine(path, "summary.json"));
            summaries.Add(await JsonSerializer.DeserializeAsync(file, SummaryJson.Default.MailSummary, cancellation)
                ?? throw new InvalidDataException("邮件摘要缺失。"));
        }
        return new(summaries.OrderByDescending(s => s.Date).ThenBy(s => s.Id).Skip(offset).Take(limit).ToArray(),
            offset, summaries.Count - offset > limit);
    }

    public async Task<Guid> Import(Guid folderId, byte[] raw, ParsedMail metadata, CancellationToken cancellation = default)
    {
        CheckFolder(folderId);
        var id = Guid.NewGuid();
        var destination = Path.Combine(directory, id.ToString("N"));
        var pending = destination + ".pending";
        Directory.CreateDirectory(pending);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(pending, "original.eml"), raw, cancellation);
            var summary = new MailSummary(id, folderId, metadata.Subject, metadata.From, metadata.To, metadata.Date, raw.LongLength);
            await using (var file = File.Create(Path.Combine(pending, "summary.json")))
                await JsonSerializer.SerializeAsync(file, summary, SummaryJson.Default.MailSummary, cancellation);
            Directory.Move(pending, destination);
            return id;
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }

    public Task<byte[]> Read(Guid messageId, CancellationToken cancellation = default) =>
        File.ReadAllBytesAsync(Path.Combine(directory, messageId.ToString("N"), "original.eml"), cancellation);

    private static void CheckFolder(Guid id)
    {
        if (id != Folder.Id) throw new ArgumentException("文件邮箱目录不存在。");
    }
}
