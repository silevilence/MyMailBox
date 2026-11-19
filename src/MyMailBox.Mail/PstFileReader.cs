using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MyMailBox.Contracts;
using MyMailBox.Pst;

namespace MyMailBox.Mail;

internal static class PstFileReader
{
    internal static MailImportReader Reader { get; } = new(new("pst", "Outlook 数据文件 (PST)", "*.pst", 1,
        [new("codepage", "旧版 ANSI 文件的源代码页（可留空，简体中文通常为 936）")]), Visit);

    private static async Task Visit(ImportReadRequest request, Func<MailImportItem,CancellationToken,Task> receive, CancellationToken token)
    {
        var value = request.Parameters.GetValueOrDefault("codepage");
        int? page = string.IsNullOrWhiteSpace(value) ? null : int.TryParse(value, out var n) && n > 0 ? n : throw new InvalidDataException("请填写有效的代码页编号。");
        await foreach (var item in Read(request.Preview ? "preview" : "resume", request.File, page, token, request.Preview ? null : request.Committed))
            await receive(item, token);
    }

    private static async IAsyncEnumerable<MailImportItem> Read(string mode, string file, int? page, [EnumeratorCancellation] CancellationToken token, IReadOnlyCollection<uint>? committed = null)
    {
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet.exe"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = committed != null, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { typeof(PstEntry).Assembly.Location, mode, file, page?.ToString() ?? "" }) start.ArgumentList.Add(arg);
        using var worker = Process.Start(start) ?? throw new IOException("无法启动 PST 读取器。");
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            if (committed != null)
            {
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(committed).AsMemory(), token);
                worker.StandardInput.Close();
            }
            while (await worker.StandardOutput.ReadLineAsync(token) is { } line)
                yield return JsonSerializer.Deserialize<MailImportItem>(line) ?? throw new InvalidDataException("PST 读取器返回无效记录。");
            await worker.WaitForExitAsync(token);
            if (worker.ExitCode != 0) throw new InvalidDataException("无法读取 PST：" + (await errors).Trim());
        }
        finally
        {
            if (!worker.HasExited) worker.Kill();
            await worker.WaitForExitAsync();
        }
    }
}
