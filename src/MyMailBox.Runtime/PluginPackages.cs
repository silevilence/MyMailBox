using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed class PluginPackages(string root, PluginCatalog catalog)
{
    public string Root { get; } = Path.GetFullPath(root);

    public PluginManifest Inspect(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少 manifest.json");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(stream, ConfigurationFiles.Json) ?? throw new InvalidDataException("manifest 为空");
        ValidateManifest(manifest);
        if (zip.Entries.Sum(p => p.Length) > 200 * 1024 * 1024) throw new InvalidDataException("包展开大小超过 200 MB");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in zip.Entries)
        {
            ValidateRelative(file.FullName);
            if (!paths.Add(file.FullName)) throw new InvalidDataException("重复路径：" + file.FullName);
            if (file.FullName == "manifest.json") continue;
            if (!manifest.Files!.TryGetValue(file.FullName, out var expected)) throw new InvalidDataException("未登记文件：" + file.FullName);
            using var content = file.Open();
            if (!Convert.ToHexString(SHA256.HashData(content)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("完整性校验失败：" + file.FullName);
            RejectShared(file.FullName);
        }
        foreach (var file in manifest.Files!.Keys)
            if (!paths.Contains(file)) throw new InvalidDataException("缺少文件：" + file);
        return manifest;
    }

    public PluginDefinition Install(string package)
    {
        var manifest = Inspect(package);
        var target = VersionDirectory(manifest.Id, manifest.Version);
        if (Directory.Exists(target)) throw new IOException("此版本已经安装，不覆盖正在使用的 DLL：" + target);
        Directory.CreateDirectory(Root);
        var stage = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = ZipFile.OpenRead(package);
            foreach (var entry in zip.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                if (!destination.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越界");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
            VerifyDirectory(stage, manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(stage, target);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        var definition = new PluginDefinition(manifest, PluginOrigin.External, target);
        catalog.Add(definition); return definition;
    }

    public IReadOnlyList<string> Discover()
    {
        var errors = new List<string>();
        if (!Directory.Exists(Root)) return errors;
        foreach (var path in Directory.EnumerateFiles(Root, "manifest.json", SearchOption.AllDirectories).Where(p => !Path.GetRelativePath(Root, p).StartsWith('.')))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), ConfigurationFiles.Json)!;
                ValidateManifest(manifest);
                var directory = Path.GetDirectoryName(path)!;
                if (!directory.Equals(VersionDirectory(manifest.Id, manifest.Version), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("包目录与 manifest 不一致");
                VerifyDirectory(directory, manifest);
                catalog.Add(new(manifest, PluginOrigin.External, directory));
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException) { errors.Add(path + "：" + e.Message); }
        }
        return errors;
    }

    public void Remove(PluginDefinition definition, PluginConfiguration ordinary, PluginConfiguration core)
    {
        if (definition.Origin != PluginOrigin.External) throw new InvalidOperationException("内置插件随应用提供，不能移除文件。");
        if (ordinary.Instances.Concat(core.Instances).Any(p => p.Plugin == definition.Manifest.Id && p.Version == definition.Manifest.Version))
            throw new InvalidOperationException("配置仍引用此版本，请先移除实例；核心版本须重启后处理。");
        var target = VersionDirectory(definition.Manifest.Id, definition.Manifest.Version);
        if (!target.Equals(Path.GetFullPath(definition.Directory), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("移除路径不匹配");
        Directory.Delete(target, true);
        catalog.Remove(definition.Manifest.Id, definition.Manifest.Version);
    }

    private string VersionDirectory(string id, string version)
    {
        if (!Regex.IsMatch(id, "^[a-z][a-z0-9.-]*$") || !Version.TryParse(version, out _)) throw new InvalidDataException("插件 ID 或版本无效");
        return Path.GetFullPath(Path.Combine(Root, id, version));
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || !Regex.IsMatch(manifest.Id, "^[a-z][a-z0-9.-]*$") || !Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("manifest.id/version 无效");
        if (manifest.SdkMin > 1 || manifest.SdkMax < 1) throw new InvalidDataException("manifest.sdkMin/sdkMax 与 SDK 1 不兼容");
        if (string.IsNullOrWhiteSpace(manifest.Entry) || manifest.Files is null || manifest.Assembly is null || !manifest.Files.ContainsKey(manifest.Assembly)) throw new InvalidDataException("manifest.entry/assembly/files 不完整");
        foreach (var path in manifest.Files.Keys) ValidateRelative(path);
    }
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is ".." or "." or "" || p.EndsWith('.') || p.EndsWith(' '))) throw new InvalidDataException("包路径越界或无效：" + path);
    }
    private static void RejectShared(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && (File.Exists(Path.Combine(AppContext.BaseDirectory, name)) || name is "CordiSharp.dll" or "MyMailBox.Contracts.dll" || name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("包包含宿主共享依赖，不能隔离同名版本：" + name);
    }
    private static void VerifyDirectory(string directory, PluginManifest manifest)
    {
        foreach (var (relative, hash) in manifest.Files!)
        {
            ValidateRelative(relative); RejectShared(relative);
            using var stream = File.OpenRead(Path.Combine(directory, relative));
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("完整性校验失败：" + relative);
        }
    }
}
