using System.Net;
using System.Text;
using System.Text.Json;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class LocalSampleUiChecks
{
    public static void Run(Ui ui, SqliteMailStore store, MailAccount account, string output)
    {
        // Seed through the normal sign-in and delta services with an in-memory transport.
        using (var http = new HttpClient(new FolderResponse()))
        using (var graph = new GraphMailService(store, new SampleAuthorization(store, account.Address), http))
        {
            graph.SignIn(account.Id).GetAwaiter().GetResult();
            graph.Sync(account.Id, new("SampleInbox", "离线样本", 0), default).GetAwaiter().GetResult();
        }
        ui.Click("nav-accounts/graph"); ui.Status("graph-status", "已登录");
        ui.Click("graph-offline"); ui.Status("graph-status", "第 1 页，共 25 条摘要");
        ui.Click("graph-offline-next"); ui.Status("graph-status", "第 2 页，共 1 条摘要");
        ui.ListContains("graph-offline-messages", "离线邮件");
        ui.Click("graph-offline-prev"); ui.Status("graph-status", "第 1 页，共 25 条摘要");
        ui.Click("graph-disconnect"); ui.Status("graph-status", "已清除");

        var file = Path.Combine(output, "original.eml");
        var raw = "From: sample@example.invalid\r\nTo: archive@example.invalid\r\nSubject: Sample EML\r\n\r\nOriginal body\r\n"u8.ToArray();
        File.WriteAllBytes(file, raw);
        ui.Click("nav-local-mail/pst-import"); ui.Select("import-format", 1);
        ui.Text("import-option-folder", "外部导入验收"); ui.Text("pst-file", file);
        ui.Click("pst-preview"); ui.Status("pst-status", "1 个目录、1 个项目");
        ui.Click("pst-start"); ui.Status("pst-status", "导入结束。已导入 1 封");
        ui.Click("pst-start"); ui.Status("pst-status", "已存在 1 封");
        var folder = store.Folders().GetAwaiter().GetResult().Single(f => f.Name == "外部导入验收");
        var item = store.List(folder.Id, 0, 10).GetAwaiter().GetResult().Items.Single();
        if (!store.Read(item.Id).GetAwaiter().GetResult().SequenceEqual(raw)) throw new Exception("External importer changed the original MIME");
        Console.WriteLine("PASS published UI: two offline Graph pages and external EML package schema/preview/import/replay with exact MIME");
    }

    private sealed class SampleAuthorization(SqliteMailStore store, string address) : IGraphAuthorization
    {
        public Task<GraphGrant> Acquire(Guid account, string clientId, string? homeAccountId, bool interactive, bool refresh, CancellationToken cancellation)
        {
            new GraphTokenCache(store).Save(account, [1]);
            return Task.FromResult(new GraphGrant(Guid.NewGuid().ToString(), "fixture", address));
        }
    }

    private sealed class FolderResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var items = Enumerable.Range(1, 26).Select(i => new { id = i.ToString("D2"), subject = $"离线邮件 {i}" });
            var content = JsonSerializer.Serialize(new Dictionary<string,object> {
                ["value"] = items, ["@odata.deltaLink"] = request.RequestUri!.GetLeftPart(UriPartial.Path) + "?cursor=fixture"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }
}
