using System.IO.Compression;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

internal static class PackageChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(PluginChecks.Repository, "artifacts/checks/packages-" + Guid.NewGuid().ToString("N"));
        var catalog = DefaultProfile.Catalog(); var packages = new PluginPackages(root, catalog);
        var input = Path.Combine(PluginChecks.Repository, "artifacts/packages/example-1.0.0.zip");
        var definition = packages.Install(input);
        await PluginChecks.Reject(() => Task.Run(() => packages.Install(input)), "已经安装");
        await using var runtime = new PluginRuntime(new Contributions());
        await runtime.Start(definition, new() { InstanceId = "installed" });
        PluginChecks.Check((await runtime.Scope("installed").Execute("ping")).Contains("私有依赖"), "Installed private dependency failed");
        await PluginChecks.Reject(() => Task.Run(() => packages.Remove(definition, new(1, [new("installed", "example", "1.0.0")]), new(1, []))), "仍引用");
        await runtime.Stop("installed");
        var bad = Path.Combine(root, "bad.zip"); File.Copy(input, bad);
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.dll").Open()); writer.Write("bad"); }
        await PluginChecks.Reject(() => Task.Run(() => packages.Inspect(bad)), "路径越界");
        var corrupt = Path.Combine(root, "corrupt.zip"); File.Copy(input, corrupt);
        using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
        { zip.GetEntry("ExamplePlugin.dll")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("ExamplePlugin.dll").Open()); writer.Write("corrupt"); }
        await PluginChecks.Reject(() => Task.Run(() => packages.Inspect(corrupt)), "完整性");
        PluginChecks.Check(packages.Discover().Count == 0, "Installed manifest discovery failed");
        var assembly = Path.Combine(definition.Directory, "ExamplePlugin.dll"); var validAssembly = File.ReadAllBytes(assembly);
        File.WriteAllText(assembly, "corrupt installed plugin");
        var recoveredCatalog = DefaultProfile.Catalog(); var recoveredPackages = new PluginPackages(root, recoveredCatalog);
        var failures = recoveredPackages.Discover();
        PluginChecks.Check(failures.Count == 1 && failures[0].Contains("完整性") && recoveredCatalog.All.All(p => p.Manifest.Id != "example"), "Corrupt installed plugin aborted discovery or remained available");
        await runtime.Start(recoveredCatalog.Find(new("welcome", "welcome", "1.0.0")), new() { InstanceId = "welcome" });
        await runtime.Stop("welcome"); File.WriteAllBytes(assembly, validAssembly);
        PluginChecks.Check(recoveredPackages.Discover().Count == 0, "Repaired plugin failed rediscovery");
        packages.Remove(definition, new(1, []), new(1, []));
        PluginChecks.Check(!Directory.Exists(definition.Directory) && File.Exists(bad), "Removed unrelated data");
        Console.WriteLine("PASS packages: hash/path checks, installed activation, private dependency, duplicate version refusal, referenced removal refusal, scoped removal");
    }
}
