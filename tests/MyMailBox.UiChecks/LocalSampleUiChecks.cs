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
            graph.Enable(account.Id, default).GetAwaiter().GetResult();
            graph.Sync(account.Id, new("SampleInbox", "离线样本", 0), default).GetAwaiter().GetResult();
        }
        ui.Click("nav-accounts/graph"); ui.Status("graph-status", "已登录");
        ui.Click("graph-offline"); ui.Status("graph-status", "第 1 页，共 25 条摘要");
        ui.Click("graph-offline-next"); ui.Status("graph-status", "第 2 页，共 1 条摘要");
        ui.ListContains("graph-offline-messages", "离线邮件");
        ui.Click("graph-offline-prev"); ui.Status("graph-status", "第 1 页，共 25 条摘要");
        ui.Click("graph-pick-move"); ui.Status("graph-status", "请选择邮件");
        ui.Click("nav-accounts/compose"); ui.Find("compose-graph-draft");
        ui.Click("nav-accounts/graph"); ui.Status("graph-status", "已登录");
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
        ui.Click("nav-accounts/manage"); ui.Click("account-new"); ui.Select("account-provider",7);
        ui.Text("account-name","A Loopback"); ui.Text("account-address","desktop@example.invalid"); ui.Click("account-save");
        ui.Status("account-status","账户已保存");
        ui.Click("nav-accounts/compose"); ui.Text("compose-to","desktop@example.invalid"); ui.Text("compose-subject","外部邮箱投递");
        ui.Text("compose-body","桌面插件正文"); ui.Click("compose-send"); ui.Status("compose-status","回环");
        ui.Click("nav-accounts/mailbox-browser"); ui.Click("provider-connect"); ui.Status("provider-status","2 个目录");
        ui.Click("provider-list"); ui.Status("provider-status","1 封"); ui.Click("provider-read"); ui.Status("provider-body","桌面插件正文");
        ui.Text("provider-query","外部"); ui.Click("provider-search"); ui.Status("provider-status","1 封");
        ui.Click("provider-mark"); ui.Status("provider-status","已标为已读"); ui.Select("provider-target",1);
        ui.Click("provider-move"); ui.Status("provider-status","已移动到"); ui.Text("provider-file",file);
        ui.Click("provider-upload"); ui.Status("provider-status","已上传邮件"); ui.Click("provider-quota"); ui.Status("provider-status","总容量");
        ui.Click("provider-delta"); ui.Status("provider-status","移出 1 封");
        ui.Click("nav-accounts/exchange"); ui.Status("exchange-status","请先");
        ui.Click("nav-accounts/manage"); ui.Click("account-new"); ui.Select("account-provider",6);
        ui.Text("account-name","公司验收样本"); ui.Text("account-address","eas@example.invalid"); ui.Click("account-save"); ui.Status("account-status","账户已保存");
        ui.Click("nav-accounts/exchange"); ui.Status("exchange-status","请配置连接");
        ui.Text("exchange-endpoint","http://exchange.example.invalid"); ui.Click("exchange-save"); ui.Status("exchange-status","HTTPS");
        ui.Text("exchange-endpoint","https://exchange.example.invalid"); ui.Text("exchange-user","fixture-user"); ui.Text("exchange-password","fixture-password");
        ui.Click("exchange-save"); ui.Status("exchange-status","设置已保存");
        ui.Click("nav-accounts/templates");
        var templates = Path.Combine(output,"profile-template.json");
        ui.Text("templates-path",templates); ui.Click("templates-export"); ui.Status("templates-status","已导出");
        ui.Click("templates-open"); ui.Status("templates-status","个条目"); ui.Select("templates-entries",Array.FindIndex(new AccountService(store).List().GetAwaiter().GetResult().ToArray(),a=>a.Provider=="local"));
        ui.Click("templates-fields"); ui.Status("templates-status","核对本机绑定");
        ui.Click("templates-preview"); ui.Status("templates-status","请映射");
        ui.Select("templates-map-0",1); ui.Click("templates-preview"); ui.Status("templates-status","已选择本机账户");
        var sourceRule = new OutlookSourceRule("桌面导入对照", 1, true, true, 0,
            [new(1, 2, "subject", ["Sample"], true, "")], [], [new(1, 19, "mark-read", [], true, "")]);
        var inventory = Path.Combine(output, "local-rule-inventory.json");
        File.WriteAllBytes(inventory, JsonSerializer.SerializeToUtf8Bytes(new OutlookRuleInventory(1, "桌面样本", "本地邮箱", [sourceRule]), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        ui.Click("nav-accounts/rules"); ui.Expand("从经典 Outlook 规则清单转换");
        ui.Text("outlook-rule-file", inventory); ui.Click("outlook-rule-open"); ui.Status("rule-status", "1 条规则");
        ui.Select("outlook-rule-scope", 0); ui.Click("outlook-rule-convert"); ui.Status("rule-status", "已载入停用草稿");
        ui.Click("rule-save"); ui.Status("rule-status", "已保存「桌面导入对照」");
        var protocols = new ProtocolMailService(store);
        var rules = new ReceiveRuleService(store, new(store, protocols), protocols);
        if (rules.Dashboard().GetAwaiter().GetResult().Rules.Single(x => x.Name == sourceRule.Name).Enabled)
            throw new Exception("Imported desktop rule was enabled without confirmation");
        ui.Click("nav-updates/settings"); ui.Select("update-source", 1); ui.Click("update-save"); ui.Status("update-status", "设置已保存");
        ui.Click("update-check"); ui.Status("update-status", "离线示例已检查版本");
        Console.WriteLine("PASS published UI: Graph offline and write access, external EML import/replay, account creation, loopback compose/send/read/search/mark/move/upload/quota/delta, ActiveSync validation, update source, rule conversion and portable templates");
    }

    private sealed class SampleAuthorization(SqliteMailStore store, string address) : IGraphAuthorization, IGraphWriteAuthorization
    {
        public Task<GraphGrant> AcquireWrite(Guid account, string clientId, string home, bool interactive, CancellationToken token) =>
            Acquire(account, clientId, home, interactive, false, token);
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
