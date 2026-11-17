using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.NaCl;
using MyMailBox.Updates;

Console.OutputEncoding = new UTF8Encoding(false);
try
{
if (args.Length != 4) throw new ArgumentException("Usage: ReleaseTools <installer-directory> <release-metadata.json> <repository> <update-source.json>");
var directory = Path.GetFullPath(args[0]);
using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
using var config = JsonDocument.Parse(await File.ReadAllTextAsync(args[3]));
var repository = GitHubReleaseSource.ValidateRepository(args[2]);
if (!string.Equals(config.RootElement.GetProperty("repository").GetString(), repository, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("发布仓库与客户端更新源不一致。");
var version = metadata.RootElement.GetProperty("version").GetString()!;
if (ReleaseVersion.Parse(version) is null) throw new InvalidDataException("发布版本无效。");
var tag = metadata.RootElement.GetProperty("triggerTag").GetString()!;
if (tag is not { Length: > 1 } || tag[0] is not ('V' or 'v') || tag[1..] != version) throw new InvalidDataException("触发 Tag 与版本不一致。");
var seedText = Environment.GetEnvironmentVariable("MYMAILBOX_ED25519_SEED");
if (string.IsNullOrWhiteSpace(seedText)) throw new InvalidDataException("缺少 MYMAILBOX_ED25519_SEED CI Secret。");
var seed = Convert.FromBase64String(seedText); Environment.SetEnvironmentVariable("MYMAILBOX_ED25519_SEED", null);
if (seed.Length != 32) throw new InvalidDataException("签名 Secret 必须为 base64 编码的 32 字节 Ed25519 seed。");
Ed25519.KeyPairFromSeed(out var publicKey, out var privateKey, seed);
try
{
    if (Convert.ToBase64String(publicKey) != config.RootElement.GetProperty("publicKey").GetString()) throw new InvalidDataException("CI 签名密钥与客户端固定公钥不匹配，拒绝发布。");
    var installerName = $"MyMailBox-V{version}-win-x64-setup.exe"; var installer = Path.Combine(directory, installerName);
    var payload = await File.ReadAllBytesAsync(installer);
    var payloadSignature = Convert.ToBase64String(Ed25519.Sign(payload, privateKey));
    var url = new Uri($"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(installerName)}");
    var appcast = SignedAppCast.Create(version, url, payload.Length, payloadSignature, metadata.RootElement.GetProperty("body").GetString()!);
    await File.WriteAllBytesAsync(Path.Combine(directory, "appcast.xml"), appcast);
    await File.WriteAllTextAsync(Path.Combine(directory, "appcast.xml.signature"), Convert.ToBase64String(Ed25519.Sign(appcast, privateKey)), new UTF8Encoding(false));
    var names = new[] { installerName, "appcast.xml", "appcast.xml.signature", "build-manifest.json", "ci-manifest.json", "publish-files.json" };
    var entries = new List<object>(); var checksums = new List<string>();
    foreach (var name in names)
    {
        var path = Path.Combine(directory, name); using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); checksums.Add(hash + "  " + name);
        entries.Add(new { name, size = stream.Length, sha256 = hash });
    }
    var sums = Path.Combine(directory, "SHA256SUMS.txt"); await File.WriteAllTextAsync(sums, string.Join('\n', checksums) + "\n", new UTF8Encoding(false));
    entries.Add(new { name = "SHA256SUMS.txt", size = new FileInfo(sums).Length, sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sums))).ToLowerInvariant() });
    await File.WriteAllTextAsync(Path.Combine(directory, "release-assets.json"), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS signed release bundle: V{version}, {entries.Count} assets; private key not persisted");
}
finally { CryptographicOperations.ZeroMemory(seed); CryptographicOperations.ZeroMemory(privateKey); }
return 0;
}
catch (Exception error) { Console.Error.WriteLine("Release signing failed: " + error.Message); return 1; }
