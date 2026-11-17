using System.Text.Json;
using MyMailBox.Updates;

if (args.Length != 1) return 2;
var job = JsonSerializer.Deserialize<UpdateJob>(await File.ReadAllTextAsync(args[0])) ?? throw new InvalidDataException("Missing update job.");
try
{
    using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "update-source.json")));
    return await UpdateHandoff.Execute(job, config.RootElement.GetProperty("publicKey").GetString() ?? "");
}
catch (Exception e) { UpdateHandoff.WriteState(job, "failed", "更新助手无法执行：" + e.Message); return 1; }
