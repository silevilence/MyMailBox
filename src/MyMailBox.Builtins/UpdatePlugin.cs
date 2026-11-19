using System.Reflection;
using System.Text.Json;
using CordiSharp;
using MyMailBox.Contracts;
using MyMailBox.Updates;

namespace MyMailBox.Builtins;

public static class UpdatePlugin
{
    public static void Apply(Context ctx, string workspace, Action<string>? notify, IUpdateSource? source = null, Action<PreparedUpdate>? install = null)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-source.json")));
        var repository = config.RootElement.GetProperty("repository").GetString() ?? throw new InvalidDataException("未配置更新仓库。");
        var current = typeof(UpdatePlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) }; // Uses the system proxy; never adds credentials or a personal token.
        var host = ctx.Get<IPluginHost>("mailbox.host")!;
        var service = new UpdateService(Path.Combine(workspace, "plugin-data", "updates"), current, repository, repo => source ?? new GitHubReleaseSource(repo, http), extensions: () => host.UpdateSources);
        var runner = new UpdateRunner(service, notify);
        var installer = new UpdatePackages(Path.Combine(workspace, "updates", "downloads"), config.RootElement.GetProperty("publicKey").GetString() ?? "", current,
            install ?? (_ => throw new InvalidOperationException("当前宿主未提供更新退出流程。")));
        ctx.Effect(() => new Cleanup(() => { runner.Dispose(); service.Dispose(); installer.Dispose(); http.Dispose(); }));
        ctx.Effect(() => host.Add(new Page("settings", "软件更新", "", [], [], (_, _) => Task.FromResult("")) { Updates = service, UpdateInstaller = installer }));
    }
}
