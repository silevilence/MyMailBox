using System.Text.Json;
using System.Text.Json.Serialization;
using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed record PluginInstance(string Id, string Plugin, string Version, bool Enabled = true, PluginOptions? Options = null, string[]? Grants = null);
public sealed record PluginConfiguration(int SchemaVersion, PluginInstance[] Instances);

public static class ConfigurationFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public static PluginConfiguration Read(string path)
    {
        try
        {
            var config = JsonSerializer.Deserialize<PluginConfiguration>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("配置为空");
            if (config.SchemaVersion == 0) config = config with { SchemaVersion = 1 };
            if (config.SchemaVersion != 1) throw new InvalidDataException("schemaVersion：仅支持版本 0 → 1 迁移或版本 1");
            if (config.Instances is null) throw new InvalidDataException("instances：必须为数组");
            var ids = new HashSet<string>();
            foreach (var item in config.Instances)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id)) throw new InvalidDataException("instances.id：为空或重复");
                if (string.IsNullOrWhiteSpace(item.Plugin)) throw new InvalidDataException($"instances[{item.Id}].plugin：不能为空");
                if (!Version.TryParse(item.Version, out _)) throw new InvalidDataException($"instances[{item.Id}].version：版本格式无效");
                if (item.Options is { Greeting.Length: > 200 }) throw new InvalidDataException($"instances[{item.Id}].options.greeting：最多 200 字符");
            }
            return config;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
        { throw new InvalidDataException($"{path}：{e.Message}", e); }
    }

    public static void Save(string path, PluginConfiguration config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, Json));
        File.Move(temporary, path, true);
    }
}

public sealed class PluginCatalog
{
    private readonly Dictionary<string, PluginDefinition> _definitions = [];
    public IReadOnlyCollection<PluginDefinition> All => _definitions.Values;
    public void Add(PluginDefinition definition) => _definitions[definition.Manifest.Id + "@" + definition.Manifest.Version] = definition;
    public void Remove(string id, string version) => _definitions.Remove(id + "@" + version);
    public PluginDefinition Find(PluginInstance instance) => _definitions.GetValueOrDefault(instance.Plugin + "@" + instance.Version)
        ?? throw new InvalidDataException($"instances[{instance.Id}] → {instance.Plugin}@{instance.Version}：缺失插件/服务或版本不兼容");

    public IReadOnlyList<PluginInstance> Order(PluginConfiguration config, PluginKind kind, IReadOnlySet<string>? availableCore = null)
    {
        var active = config.Instances.Where(p => p.Enabled).ToArray();
        var result = new List<PluginInstance>();
        var visiting = new List<string>();
        var done = new HashSet<string>();
        foreach (var instance in active) Visit(instance);
        return result;

        void Visit(PluginInstance instance)
        {
            if (done.Contains(instance.Id)) return;
            if (visiting.Contains(instance.Plugin)) throw new InvalidDataException("循环依赖：" + string.Join(" → ", visiting.Append(instance.Plugin)));
            var definition = Find(instance);
            if (definition.Manifest.Kind != kind) throw new InvalidDataException($"instances[{instance.Id}]：{kind} 配置不能包含 {definition.Manifest.Kind} 插件");
            if (definition.Manifest.SdkMin > 1 || definition.Manifest.SdkMax < 1) throw new InvalidDataException($"instances[{instance.Id}] → {instance.Plugin}：SDK 版本不兼容");
            visiting.Add(instance.Plugin);
            foreach (var dep in definition.Manifest.Dependencies ?? [])
            {
                if (availableCore?.Contains(dep) == true) continue;
                var dependency = active.FirstOrDefault(p => p.Plugin == dep)
                    ?? throw new InvalidDataException("缺失依赖服务：" + string.Join(" → ", visiting.Append(dep)));
                Visit(dependency);
            }
            visiting.RemoveAt(visiting.Count - 1);
            done.Add(instance.Id); result.Add(instance);
        }
    }
}

public sealed class CoreProfile(PluginRuntime runtime, PluginCatalog catalog, string path)
{
    private bool _started;
    public IReadOnlySet<string> ActivePlugins { get; private set; } = new HashSet<string>();
    public string? Error { get; private set; }
    public async Task Start()
    {
        if (_started) throw new InvalidOperationException("核心 Profile 仅允许启动时读取；请重启应用。");
        _started = true;
        var started = new List<string>();
        try
        {
            var ordered = catalog.Order(ConfigurationFiles.Read(path), PluginKind.Core);
            foreach (var item in ordered)
            {
                await runtime.Start(catalog.Find(item), Options(item));
                started.Add(item.Id);
            }
            ActivePlugins = ordered.Select(p => p.Plugin).ToHashSet();
        }
        catch (Exception e)
        {
            foreach (var id in started.AsEnumerable().Reverse()) await runtime.Stop(id);
            Error = $"核心 Profile 启动失败，已进入安全模式。修复 {path} 后重启。\n{e.Message}";
        }
    }
    public static PluginOptions Options(PluginInstance instance) => new()
    {
        InstanceId = instance.Id, Greeting = instance.Options?.Greeting ?? "你好",
        FailInitialization = instance.Options?.FailInitialization ?? false,
        Grants = instance.Grants ?? []
    };
}
