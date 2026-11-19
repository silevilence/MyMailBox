using System.Text.Json;

namespace MyMailBox.Runtime;

public sealed record DiagnosticEvent(DateTimeOffset Time, string Instance, string Operation, string Outcome, string CorrelationId, string? ErrorCode);

/// <summary>Allowlisted metadata only; no exception text, config, mail body or credentials.</summary>
public sealed class Diagnostics(string directory, int maximumBytes = 262144, int retainedFiles = 3)
{
    private readonly object _gate = new();
    public long MissedRecords { get; private set; }
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string Record(string instance, string operation, string outcome, string? errorCode = null)
    {
        var correlation = Guid.NewGuid().ToString("N");
        var item = new DiagnosticEvent(DateTimeOffset.UtcNow, SafeId(instance), SafeId(operation), outcome, correlation, errorCode);
        lock (_gate)
        {
            try { Append(item); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MissedRecords++; }
        }
        return correlation;
    }
    private void Append(DiagnosticEvent item)
    {
        Directory.CreateDirectory(DirectoryPath);
        var current = Path.Combine(DirectoryPath, "diagnostics.jsonl");
        if (File.Exists(current) && new FileInfo(current).Length >= maximumBytes)
        {
            for (var i = retainedFiles - 1; i >= 1; i--)
            {
                var source = i == 1 ? current : current + "." + (i - 1);
                if (File.Exists(source)) File.Move(source, current + "." + i, true);
            }
        }
        File.AppendAllText(current, JsonSerializer.Serialize(item, new JsonSerializerOptions(ConfigurationFiles.Json) { WriteIndented = false }) + Environment.NewLine);
    }
    public string Export(string path)
    {
        lock (_gate)
        {
            var events = Directory.Exists(DirectoryPath)
                ? Directory.EnumerateFiles(DirectoryPath, "diagnostics.jsonl*").SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<DiagnosticEvent>(line, ConfigurationFiles.Json)).Where(item => item is not null).OrderBy(item => item!.Time).ToArray()
                : [];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(events, ConfigurationFiles.Json));
            return path;
        }
    }
    private static string SafeId(string id) => id.Length <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/') ? id : "unrecognized-id";
}

public sealed class HostOperations
{
    private readonly Dictionary<string, Func<string?, CancellationToken, Task<string>>> _handlers = [];
    public void Register(string capability, Func<string?, CancellationToken, Task<string>> operation) => _handlers.Add(capability, operation);
    internal Task<string> Invoke(string capability, string? account, CancellationToken cancellation) => _handlers.TryGetValue(capability, out var operation)
        ? operation(account, cancellation) : throw new NotSupportedException("宿主尚未提供此能力：" + capability);
}

public sealed class PermissionPolicy(string[] declared, string[] granted)
{
    public void Require(string capability, string? account = null)
    {
        if (!declared.Contains(capability) || !(granted.Contains(capability) || account is not null && granted.Contains(capability + ":" + account)))
            throw new UnauthorizedAccessException($"未授予权限：{capability}{(account is null ? "" : ":" + account)}");
    }
}
