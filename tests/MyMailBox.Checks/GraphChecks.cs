using System.Net;
using System.Text;
using Microsoft.Identity.Client;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class GraphChecks
{
    public static async Task Run()
    {
        var workspace = Path.Combine(PluginChecks.Repository, "artifacts/checks/graph-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(Path.Combine(workspace, "mail")); var accounts = new AccountService(store);
        var account = await accounts.Save(new(null, "个人测试", "outlook-com", "test@outlook.com"));
        Check(account.Capabilities == (AccountCapabilities.Receive | AccountCapabilities.Folders), "Graph must be read-only");
        var auth = new Authorization(new(store)); var handler = new Handler(); using var http = new HttpClient(handler);
        var clientId = GraphApplication.ClientId.Length > 0 ? GraphApplication.ClientId : Guid.NewGuid().ToString();
        using (var unconfigured = new GraphMailService(store, auth, http, ""))
            await Reject(() => unconfigured.SignIn(account.Id), "尚未配置");
        using var service = new GraphMailService(store, auth, http, clientId);
        await service.SignIn(account.Id);
        Check(auth.LastClient == clientId, "Application ID not used for direct login");
        var second = await accounts.Save(new(null, "另一位用户", "outlook-com", "second@outlook.com"));
        auth.Username = "second@outlook.com"; await service.SignIn(second.Id);
        Check(auth.LastClient == clientId && (await service.Settings(second.Id)).SignedIn, "Second user could not use the shared application registration");
        await service.Disconnect(second.Id); await accounts.Remove(second.Id); auth.Username = "test@outlook.com";
        Check((await service.Settings(account.Id)).SignedIn, "Sign in not persisted");
        var cache = new GraphTokenCache(store); Check(Encoding.UTF8.GetString(cache.Load(account.Id)!) == "synthetic-msal-cache", "Protected cache did not reopen");
        using (var db = store.Open())
        {
            using var query = db.CreateCommand(); query.CommandText = "SELECT protected FROM credentials";
            Check(!Encoding.UTF8.GetString((byte[])query.ExecuteScalar()!).Contains("synthetic-msal-cache"), "Plain cache was stored");
        }
        handler.Respond = request => Json("""{"value":[{"id":"inbox","displayName":"收件箱","childFolderCount":1}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders?$skip=1"}""");
        var folders = await service.Folders(account.Id); Check(folders.Items[0].Name == "收件箱" && folders.Next is not null, "Folder page failed");
        await service.Folders(account.Id, next: folders.Next);
        handler.Respond = _ => Json("""{"value":[{"id":"nested","displayName":"子目录","childFolderCount":0}]}""");
        await service.Folders(account.Id, "inbox"); Check(handler.Last!.AbsolutePath.EndsWith("inbox/childFolders"), "Nested folder route failed");
        handler.Respond = _ => Json("""{"value":[{"id":"mail/+1","subject":"中文主题","from":{"emailAddress":{"address":"sender@example.test"}},"receivedDateTime":"2026-11-17T01:00:00Z","isRead":false,"hasAttachments":true}]}""");
        var page = await service.Messages(account.Id, "inbox"); Check(page.Items.Single() is { IsRead: false, HasAttachments: true, Subject: "中文主题" }, "Summary lost fields");
        Check(handler.Last!.Query.Contains("%24select") || handler.Last.Query.Contains("$select"), "Summary requested full body");
        var count = handler.Requests;
        await Reject(() => service.Folders(account.Id, next: "https://example.test/v1.0/me/mailFolders"), "分页");
        Check(handler.Requests == count, "Sent token to untrusted next link");
        handler.Respond = _ => Json("""{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/messages"}""");
        await Reject(() => service.Folders(account.Id), "分页");
        var round = 0; handler.Respond = _ => ++round == 1 ? new(HttpStatusCode.Unauthorized) : Json("""{"value":[]}""");
        await service.Folders(account.Id); Check(round == 2 && auth.Refreshes == 1, "401 did not refresh exactly once");
        handler.Respond = _ => new(HttpStatusCode.Unauthorized); await Reject(() => service.Folders(account.Id), "重新登录");
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
        {
            handler.Respond = _ => new(status); await Reject(() => service.Folders(account.Id), "");
        }
        auth.Failure = new MsalUiRequiredException("invalid_grant", "remote-secret-must-not-leak"); await Reject(() => service.Folders(account.Id), "重新登录"); auth.Failure = null;
        auth.Failure = new MsalClientException("authentication_canceled", "sensitive"); await Reject(() => service.SignIn(account.Id), "取消"); auth.Failure = null;
        auth.Failure = new MsalServiceException("access_denied", "sensitive-consent-denial"); await Reject(() => service.SignIn(account.Id), "未完成"); auth.Failure = null;
        handler.WaitForCancellation = true;
        using (var canceled = new CancellationTokenSource())
        {
            var pending = service.Folders(account.Id, cancellation: canceled.Token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); canceled.Cancel();
            try { await pending; throw new Exception("In-flight cancellation ignored"); } catch (OperationCanceledException) { }
        }
        handler.WaitForCancellation = false;
        handler.Respond = _ => throw new HttpRequestException("sensitive-remote-error"); await Reject(() => service.Folders(account.Id), "网络");
        handler.Respond = _ => Json("invalid-json"); await Reject(() => service.Folders(account.Id), "格式");
        var raw = Encoding.UTF8.GetBytes("From: sender@example.test\r\nTo: test@outlook.com\r\nSubject: fixture\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=x\r\n\r\n--x\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n中文正文\r\n--x\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=test.bin\r\nContent-Transfer-Encoding: base64\r\n\r\nAQID\r\n--x--\r\n");
        handler.Respond = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };
        var id = await service.Download(account.Id, "mail/+1"); var again = await service.Download(account.Id, "mail/+1");
        Check(id == again && (await store.Read(id)).SequenceEqual(raw), "MIME download was not exact or deduplicated");
        var parsed = await new MimeReader().Parse(await store.Read(id)); Check(parsed.Text.Contains("中文正文") && parsed.Parts!.Any(p => p.Attachment), "Downloaded attachment unreadable");
        handler.Respond = _ => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; response.Content.Headers.ContentLength = MimeReader.MaxMessageBytes + 1; return response; };
        await Reject(() => service.Download(account.Id, "large"), "大小");
        using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); try { await service.Folders(account.Id, cancellation: canceled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { } }
        var backup = await new WorkspaceBackup(workspace, store).Create(Path.Combine(workspace, "backup.zip"));
        var restored = await new WorkspaceBackup(workspace, store).Restore(backup, workspace + "-restored");
        using (var restoredService = new GraphMailService(new SqliteMailStore(Path.Combine(restored, "mail")), auth, http, clientId))
            Check(!(await restoredService.Settings(account.Id)).SignedIn, "Portable backup retained Graph authorization");
        await service.Disconnect(account.Id); Check(!(await service.Settings(account.Id)).SignedIn && cache.Load(account.Id) is null, "Disconnect leaked cache");
        Check((await store.Read(id)).SequenceEqual(raw), "Disconnect removed downloaded mail");
        auth.Username = "other@outlook.com"; await Reject(() => service.SignIn(account.Id), "不一致"); Check(cache.Load(account.Id) is null, "Wrong account cache retained"); auth.Username = "test@outlook.com";
        await service.SignIn(account.Id);
        using (var changed = new GraphMailService(store, auth, http, Guid.NewGuid().ToString()))
        {
            Check(!(await changed.Settings(account.Id)).SignedIn, "Changed publisher registration retained login");
            await Reject(() => changed.Folders(account.Id), "尚未连接");
            auth.CheckEmptyCache = true; await changed.SignIn(account.Id); auth.CheckEmptyCache = false;
            Check((await changed.Settings(account.Id)).SignedIn && !(await service.Settings(account.Id)).SignedIn, "Old registration cache accepted by current application");
        }
        auth.CheckEmptyCache = true; await service.SignIn(account.Id); auth.CheckEmptyCache = false;
        Check(auth.LastClient == clientId, "Legacy registration was not migrated to publisher ID");
        await service.SignIn(account.Id); await accounts.Remove(account.Id); Check(cache.Load(account.Id) is null, "Remove retained cache");
        await Reject(() => service.Folders(account.Id), "请选择");
        await Reject(() => new ProtocolMailService(store).Settings(account.Id), "不支持");
        Console.WriteLine("PASS graph: read-only capability, DPAPI/cache/backup, identity binding, pagination, MIME+attachment, 401 refresh, errors/cancel, disconnect/removal; real personal login NOT exercised");
    }
    private static void Check(bool condition, string error) => MailChecks.Check(condition, error);
    private static async Task Reject(Func<Task> action, string text)
    {
        try { await action(); }
        catch (InvalidDataException e) when (e.Message.Contains(text) && !e.Message.Contains("sensitive") && !e.Message.Contains("remote-secret")) { return; }
        throw new Exception("Expected Graph rejection: " + text);
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Authorization(GraphTokenCache cache) : IGraphAuthorization
    {
        public string Username = "test@outlook.com"; public int Refreshes; public Exception? Failure; public string? LastClient; public bool CheckEmptyCache;
        public Task<GraphGrant> Acquire(Guid account, string clientId, string? homeAccountId, bool interactive, bool refresh, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); if (Failure is not null) throw Failure;
            LastClient = clientId;
            if (CheckEmptyCache) Check(cache.Load(account) is null && homeAccountId is null, "Old application token cache was reused");
            if (refresh) Refreshes++; cache.Save(account, Encoding.UTF8.GetBytes("synthetic-msal-cache"));
            return Task.FromResult(new GraphGrant("synthetic-access-token", "synthetic-home", Username));
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => Json("{\"value\":[]}"); public int Requests; public Uri? Last;
        public bool WaitForCancellation; public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get, "Graph sent a write request"); Check(request.Headers.Authorization?.Parameter == "synthetic-access-token", "Token missing");
            Requests++; Last = request.RequestUri;
            if (WaitForCancellation) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return Respond(request);
        }
    }
}
