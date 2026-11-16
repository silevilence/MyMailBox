namespace MyMailBox.Contracts;

public enum PluginKind { Core, Ordinary }
public enum PluginOrigin { Builtin, External }
public sealed record PluginManifest(string Id, string Version, int SdkMin, int SdkMax, PluginKind Kind,
    string Entry, string? Assembly = null, string[]? Dependencies = null, string[]? Permissions = null,
    Dictionary<string, string>? Files = null, string Source = "本地插件");

public sealed class PluginOptions
{
    public string InstanceId { get; set; } = "";
    public string Greeting { get; set; } = "你好";
    public bool FailInitialization { get; set; }
    public string[] Grants { get; set; } = [];
}

public interface IPluginHost : IContributions
{
    string InstanceId { get; }
    IDisposable Command(string id, Func<CancellationToken, Task<string>> execute);
    IDisposable Subscribe(Action<string> listener);
    void Notify(string value);
    Task<string> Invoke(string capability, string? account = null, CancellationToken cancellation = default);
}
