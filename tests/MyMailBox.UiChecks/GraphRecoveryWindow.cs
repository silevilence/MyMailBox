using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using FlaUI.UIA3;
using MyMailBox.Contracts;
using MyMailBox.Desktop;
using MyMailBox.Mail;

public sealed class GraphRecoveryWindow : Avalonia.Application
{
    private static GraphPage _page = null!;
    public static void Show(string workspace)
    {
        var store = new SqliteMailStore(Path.Combine(workspace, "mail"));
        var accounts = new AccountService(store);
        var account = accounts.Save(new(null, "本地恢复样本", "outlook-com", "sample@example.invalid")).GetAwaiter().GetResult();
        using var http = new HttpClient(new Transport(workspace));
        using var graph = new GraphMailService(store, new LocalSampleUiChecks.SampleAuthorization(store, account.Address), http);
        graph.SignIn(account.Id).GetAwaiter().GetResult();
        graph.Sync(account.Id, new("SampleInbox", "测试收件箱", 0), default).GetAwaiter().GetResult();
        _page = new(accounts, graph) { Sync = graph };
        AppBuilder.Configure<GraphRecoveryWindow>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        var view = new GraphView(_page);
        var window = new Avalonia.Controls.Window { Title = "Graph 本地恢复样本", Content = view, Width = 900, Height = 680 };
        window.Closed += (_, _) => view.Dispose();
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }
    private sealed class Transport(string workspace) : HttpMessageHandler
    {
        private int _round;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var round = ++_round;
            if (round == 2)
            {
                File.WriteAllText(Path.Combine(workspace, "sync-started"), "");
                while (!File.Exists(Path.Combine(workspace, "disconnect"))) await Task.Delay(40, token);
                throw new HttpRequestException("fixture disconnected");
            }
            var messages = round == 1 ? Enumerable.Range(1, 26) : [27];
            var content = new Dictionary<string, object>
            {
                ["value"] = messages.Select(i => new { id = i.ToString(), subject = $"样本 {i}" }),
                ["@odata.deltaLink"] = request.RequestUri!.GetLeftPart(UriPartial.Path) + "?cursor=" + round
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(content),System.Text.Encoding.UTF8,"application/json") };
        }
    }
    public static void Verify(string output)
    {
        var workspace = Path.Combine(output, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("graph-recovery-window"); start.ArgumentList.Add(workspace);
        using var automation = new UIA3Automation();
        using var app = FlaUI.Core.Application.Launch(start);
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(25)) ?? throw new Exception("Graph test window missing");
            var ui = new Ui(window);
            ui.Status("graph-status", "已登录"); ui.Click("graph-offline"); ui.Status("graph-cache-status", "共 25 条摘要");
            ui.Click("graph-resume-scope"); ui.Status("graph-status", "正在同步");
            if (!SpinWait.SpinUntil(() => File.Exists(Path.Combine(workspace,"sync-started")), 5000)) throw new Exception("Sync request missing");
            ui.Click("graph-offline-next"); ui.Status("graph-cache-status", "第 2 页，共 1 条摘要");
            ui.Status("graph-cache-status", "上次成功同步"); ui.Status("graph-status", "正在同步");
            File.WriteAllText(Path.Combine(workspace, "disconnect"), "");
            ui.Status("graph-status", "无法连接"); ui.Status("graph-cache-status", "第 2 页");
            ui.Click("graph-resume-scope"); ui.Status("graph-status", "同步完成，共 27 封");
            ui.Click("graph-offline"); ui.Click("graph-offline-next"); ui.Status("graph-cache-status", "第 2 页，共 2 条摘要");
            window.Close();
            Console.WriteLine("PASS Graph desktop: read cached pages while sync is pending, retain scope and last success offline, reconnect from saved scope and receive new mail");
        }
        finally { if (!app.HasExited) app.Kill(); }
    }
}
