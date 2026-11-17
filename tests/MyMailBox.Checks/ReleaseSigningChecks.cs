using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Chaos.NaCl;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

internal static class ReleaseSigningChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(PluginChecks.Repository, "artifacts/checks/signing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var seed = RandomNumberGenerator.GetBytes(32); var publicKey = Convert.ToBase64String(Ed25519.PublicKeyFromSeed(seed));
        var installer = Path.Combine(root, "MyMailBox-V1.2.3-win-x64-setup.exe"); await File.WriteAllBytesAsync(installer, RandomNumberGenerator.GetBytes(4096));
        foreach (var name in new[] { "build-manifest.json", "ci-manifest.json", "publish-files.json" }) await File.WriteAllTextAsync(Path.Combine(root, name), "{}");
        var meta = Path.Combine(root, "metadata.json"); await File.WriteAllTextAsync(meta, JsonSerializer.Serialize(new { version = "1.2.3", tag = "V1.2.3", triggerTag = "v1.2.3", body = "### ✨ 新功能\n\n- 签名测试。" }));
        var config = Path.Combine(root, "update-source.json"); await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { repository = "fixture/repository", publicKey }));
        async Task<(int Exit, string Output)> RunTool()
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(PluginChecks.Repository, "tools/MyMailBox.ReleaseTools/bin/Debug/net10.0/MyMailBox.ReleaseTools.dll"));
            foreach (var arg in new[] { root, meta, "fixture/repository", config }) start.ArgumentList.Add(arg);
            start.Environment["MYMAILBOX_ED25519_SEED"] = Convert.ToBase64String(seed);
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); return (process.ExitCode, await stdout + await stderr);
        }
        var result = await RunTool(); PluginChecks.Check(result.Exit == 0, result.Output);
        var cast = Path.Combine(root, "appcast.xml"); var signature = await File.ReadAllTextAsync(cast + ".signature");
        var checker = new Ed25519Checker(SecurityMode.Strict, publicKey, publicKeyFile: null);
        PluginChecks.Check(checker.VerifySignatureOfFile(signature, cast) == ValidationResult.Valid, "CLI appcast signature");
        var xml = System.Xml.Linq.XDocument.Load(cast); System.Xml.Linq.XNamespace ns = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        var enclosure = xml.Descendants("enclosure").Single();
        PluginChecks.Check(enclosure.Attribute("url")!.Value.Contains("/v1.2.3/") && checker.VerifySignatureOfFile(enclosure.Attribute(ns + "signature")!.Value, installer) == ValidationResult.Valid, "CLI preserves trigger tag and signs actual installer");
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "release-assets.json")));
        PluginChecks.Check(inventory.RootElement.GetArrayLength() == 7, "CLI complete asset set");
        foreach (var entry in inventory.RootElement.EnumerateArray())
        {
            var file = Path.Combine(root, entry.GetProperty("name").GetString()!); var bytes = await File.ReadAllBytesAsync(file);
            PluginChecks.Check(bytes.LongLength == entry.GetProperty("size").GetInt64() && Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "CLI asset digest");
        }
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { repository = "fixture/repository", publicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        var rejected = await RunTool(); PluginChecks.Check(rejected.Exit != 0 && rejected.Output.Contains("不匹配") && !rejected.Output.Contains(Convert.ToBase64String(seed)), "CLI refuses untrusted key without logging secret: exit=" + rejected.Exit + " " + rejected.Output.Replace(Convert.ToBase64String(seed), "[redacted]"));
        CryptographicOperations.ZeroMemory(seed);
        Console.WriteLine("PASS release signing CLI: ephemeral key, fixed public key match, installer/appcast Ed25519, lowercase tag URLs, seven asset hashes; no external Release mutated");
    }
}
