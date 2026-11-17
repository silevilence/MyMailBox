using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XstReader;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Length != 2) throw new ArgumentException("Usage: MyMailBox.PstProbe <pst> <report.json>");
var path = Path.GetFullPath(args[0]);
string Hash() { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
var before = Hash(); var clock = Stopwatch.StartNew(); var errors = new List<string>();
var folders = new List<object>(); var messages = new List<object>(); var file = new XstFile(path);
var messageCount = 0; var attachments = 0; long attachmentBytes = 0; var embedded = 0;
object Inspect(Message message, string folder, int depth)
{
    var files = new List<object>();
    foreach (var attachment in message.Attachments)
    {
        try
        {
            if (attachment.IsEmail)
            {
                embedded++;
                if (depth >= 8) { errors.Add("Embedded nesting limit reached: " + message.Subject); continue; }
                files.Add(new { type = "message", content = Inspect(file.OpenAttachedMessage(attachment), folder, depth + 1) });
            }
            else if (attachment.IsFile)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var output = new HashSink(hash); file.SaveAttachment(output, attachment);
                attachments++; attachmentBytes += output.Count;
                files.Add(new { type = "file", name = attachment.FileName, cid = attachment.ContentId, bytes = output.Count, sha256 = Convert.ToHexString(hash.GetHashAndReset()) });
            }
            else files.Add(new { type = "unsupported", name = attachment.FileName, method = attachment.Type });
        }
        catch (Exception error) { errors.Add($"Attachment {attachment.FileName}: {error.GetType().Name}: {error.Message}"); }
    }
    string rtf = null;
    if (message.RtfCompressed is { Length: > 0 })
    {
        using var stream = new RtfDecompressor().Decompress(message.RtfCompressed, false);
        using var reader = new StreamReader(stream); rtf = reader.ReadToEnd();
    }
    return new { folder, subject = message.Subject, text = message.Body, html = message.GetBodyAsHtmlString(), rtf,
        recipients = message.Recipients.Select(r => r.ToString()).ToArray(), files };
}
void Walk(Folder folder)
{
    folders.Add(new { folder.Path, folder.ContentCount });
    try
    {
        foreach (var message in file.ReadMessages(folder))
        {
            messageCount++;
            try { file.ReadMessageDetails(message); messages.Add(Inspect(message, folder.Path, 0)); }
            catch (Exception error) { errors.Add($"Message {message.Subject}: {error.GetType().Name}: {error.Message}"); }
            finally { message.ClearContents(); }
        }
    }
    catch (Exception error) { errors.Add($"Folder {folder.Path}: {error.GetType().Name}: {error.Message}"); }
    foreach (var child in folder.Folders) Walk(child);
}
try { Walk(file.ReadFolderTree()); } catch (Exception error) { errors.Add($"File: {error.GetType().Name}: {error.Message}"); }
clock.Stop(); var after = Hash();
var report = new { file = Path.GetFileName(path), bytes = new FileInfo(path).Length, sourceSha256 = before, sourceUnchanged = before == after,
    sourceCommit = "3b7856c8b3d01bdf8aa13744e476d1ef7761b832", runtime = Environment.Version.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    seconds = clock.Elapsed.TotalSeconds, peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64, messageCount, attachments, attachmentBytes, embedded, folders, messages, errors };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PST {report.file}: {messageCount} items; {attachments} attachments; {embedded} embedded; {errors.Count} errors; {report.seconds:F2}s; source unchanged={report.sourceUnchanged}");
if (!report.sourceUnchanged) throw new IOException("Source PST changed during read-only probe");
return errors.Count == 0 ? 0 : 2;

sealed class HashSink(IncrementalHash hash) : Stream
{
    public long Count { get; private set; }
    public override void Write(byte[] buffer, int offset, int count) { hash.AppendData(buffer, offset, count); Count += count; }
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => Count; public override long Position { get => Count; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
