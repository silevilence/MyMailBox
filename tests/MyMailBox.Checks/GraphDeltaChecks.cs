using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class GraphChecks
{
    public static async Task Delta()
    {
        var store = new SqliteMailStore(Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "graph-delta"));
        var accounts = new AccountService(store);
        var account = await accounts.Save(new(null, "增量验收", "outlook-com", "delta@example.invalid"));
        var auth = new Authorization(new(store)) { Username = account.Address };
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = Guid.NewGuid().ToString();
        using var graph = new GraphMailService(store, auth, http, client);
        await graph.SignIn(account.Id);
        var folder = new GraphFolder("InboxId", "收件箱", 0);
        var route = "https://graph.microsoft.com/v1.0/me/mailfolders('InboxId')/messages/delta?";
        HttpResponseMessage Page(object[] values, string key, string link) => Json(JsonSerializer.Serialize(new Dictionary<string,object> { ["value"] = values, [key] = link }));
        var requests = 0;
        handler.Respond = r => ++requests switch
        {
            1 => Page([new { id = "id1", subject = "原主题", isRead = false }], "@odata.nextLink", route + "$skiptoken=page2"),
            2 => throw new HttpRequestException("offline"),
            _ => throw new Exception("Extra request during offline test")
        };
        await Reject(() => graph.Sync(account.Id, folder, default), "网络");
        Check((await graph.Cached(account.Id, folder.Id, 0, default)).Items.Single().Subject == "原主题", "First delta page not committed");
        using var reopened = new GraphMailService(store, auth, http, client);
        handler.Respond = r => r.RequestUri!.Query.Contains("page2")
            ? Page([new { id="id2",subject="第二封",hasAttachments=true }], "@odata.deltaLink", route+"$deltatoken=round1")
            : throw new Exception("Restart repeated the initial request");
        var done = await reopened.Sync(account.Id, folder, default);
        Check(done.Messages == 2 && done.LastSuccess.HasValue, "Full delta round not completed");
        var raw = MailChecks.Sample("独立归档");
        handler.Respond = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };
        var saved = await reopened.Download(account.Id, "id1");
        var total = handler.Requests;
        Check(await reopened.Download(account.Id, "id1") == saved && handler.Requests == total, "Downloaded MIME more than once");
        handler.Respond = _ => Page([new { id="id1",isRead=true },new Dictionary<string,object>{["id"]="id2",["@removed"]=new {reason="deleted"}}],
            "@odata.deltaLink",route+"$deltatoken=round2");
        var renamed = await reopened.Sync(account.Id, folder with { Name = "新名称" }, default);
        var remaining = (await reopened.Cached(account.Id, folder.Id, 0, default)).Items.Single();
        Check(renamed.Name == "新名称" && remaining is { Id: "id1", Subject: "原主题", IsRead: true }, "Partial update or deletion lost metadata");
        requests = 0;
        handler.Respond = _ => ++requests == 1 ? new(HttpStatusCode.Gone) : Page([new {id="id3",subject="重建"}],"@odata.deltaLink",route+"$deltatoken=new");
        await reopened.Sync(account.Id, folder, default);
        Check((await reopened.Cached(account.Id, folder.Id, 0, default)).Items.Single().Id == "id3" && (await store.Read(saved)).SequenceEqual(raw), "Token reset lost archive or retained stale server rows");
        handler.Respond = _ => Page([], "@odata.nextLink", route.Replace("InboxId","Other")+"$skiptoken=unrelated");
        await Reject(() => reopened.Sync(account.Id, folder, default), "目录");
        handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        };
        await Reject(() => reopened.Sync(account.Id, folder, default), "限流");
        total = handler.Requests;
        await Reject(() => graph.Sync(account.Id, folder, default), "限流");
        Check(handler.Requests == total && (await graph.Cached(account.Id, folder.Id, 0, default)).Items.Count == 1, "Rate limit not persisted or offline read reached network");
        Console.WriteLine("PASS Graph delta fixture: opaque paging, process reopen, partial updates, rename/deletion, 410 rebuild, stable local MIME, scope checks and persisted Retry-After");
    }
}
