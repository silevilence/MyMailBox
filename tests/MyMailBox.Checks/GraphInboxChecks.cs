using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class GraphChecks
{
    internal static async Task InboxFaults()
    {
        var store = new SqliteMailStore(Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "graph-inbox"));
        var accounts = new AccountService(store);
        var account = await accounts.Save(new(null, "统一收件", "outlook-com", "inbox@example.invalid"));
        var auth = new Authorization(new(store)) { Username = account.Address };
        var handler = new Handler();
        using var http = new HttpClient(handler);
        using var graph = new GraphMailService(store, auth, http, Guid.NewGuid().ToString());
        var inbox = new UnifiedInboxService(store, accounts, new GraphReceiveProvider(store, graph));
        using var runner = new ReceiveRunner(inbox);
        async Task<ReceiveStatus> Refresh()
        {
            await inbox.Refresh(account.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var status = (await inbox.Status(timeout.Token)).Single(x => x.Account == account.Id);
                if (status.State is not ("queued" or "running")) return status;
                await Task.Delay(50, timeout.Token);
            }
        }
        Check((await Refresh()).State == "auth", "Unconnected Graph account not classified");
        await graph.SignIn(account.Id);
        await inbox.Configure(account.Id, false, 1);
        const string route = "https://graph.microsoft.com/v1.0/me/mailFolders/F/messages/delta?";
        var raw = MailChecks.Sample("统一 Graph 原文");
        int pageNumber = 0;
        HttpResponseMessage Page(string id, bool final, string cursor) => Json(JsonSerializer.Serialize(new Dictionary<string,object>
        {
            ["value"] = new[]{new {id,subject=id,receivedDateTime="2026-01-01T00:00:00Z"}},
            [final ? "@odata.deltaLink" : "@odata.nextLink"] = route + cursor
        }));
        HttpResponseMessage Folder() => Json("""{"id":"F","displayName":"收件箱","childFolderCount":0}""");
        handler.Respond = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/inbox")) return Folder();
            Check(request.Headers.GetValues("Prefer").Any(s => s.Contains("odata.maxpagesize=1")), "Configured batch limit missing");
            if (++pageNumber == 1) return Page("first", false, "$skiptoken=carry");
            Check(request.RequestUri.Query.Contains("carry"), "Did not resume saved unified cursor");
            return Page("second", true, "$deltatoken=saved");
        };
        Check((await Refresh()).State == "ready" && pageNumber == 1, "First bounded page failed");
        Check((await Refresh()).State == "ready" && pageNumber == 2, "Second bounded page failed");
        var page = await inbox.Inbox(null, null, 1);
        var first = page.Messages.Single();
        var second = (await inbox.Inbox(null, first.Position, 1)).Messages.Single();
        Check(page.HasMore && first.Position != second.Position && !first.Cached, "Graph keyset skipped equal-date rows");
        handler.Respond = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };
        var text = await inbox.Open(account.Id, first.Position.Key);
        var drafts = new MailDraftStore(store);
        Check(drafts.SourceAccount(first.Position.Key) == account.Id, "Graph reply source lost its sending identity");
        var sender = await accounts.Save(new(null, "转发身份", "smtp", "sender@example.invalid"));
        Check((await drafts.Respond(new(first.Position.Key, ReplyMode.Forward),sender.Id)).Subject.Contains("统一 Graph 原文"), "Graph forward failed to use original MIME");
        handler.Respond = _ => throw new HttpRequestException("private server diagnostic");
        var status = await Refresh();
        var requests = handler.Requests;
        Check(status.State == "offline" && !status.Detail.Contains("private") && await inbox.Open(account.Id, first.Position.Key) == text &&
            handler.Requests == requests, "Offline restore or error redaction failed");
        handler.Respond = _ => new(HttpStatusCode.Unauthorized);
        Check((await Refresh()).State == "auth", "Revoked consent not classified");
        await graph.SignIn(account.Id);

        using var db = store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE capacity(id INTEGER PRIMARY KEY AUTOINCREMENT);
            INSERT INTO capacity VALUES(9223372036854775807);
            CREATE TRIGGER disk_full BEFORE UPDATE OF cursor ON graph_scopes WHEN NEW.cursor LIKE '%disk%' BEGIN
                INSERT INTO capacity VALUES(NULL);
            END;
            """;
        cmd.ExecuteNonQuery();
        handler.Respond = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/inbox")) return Folder();
            Check(request.RequestUri.Query.Contains("saved"), "Storage failure committed new progress");
            return Page("after-full", true, "$deltatoken=disk");
        };
        Check((await Refresh()).State == "storage" && (await inbox.Inbox(null, null, 10)).Messages.Count == 2, "Disk-full advanced inbox cursor or left partial rows");
        cmd.CommandText = "DROP TRIGGER disk_full"; cmd.ExecuteNonQuery();
        Check((await Refresh()).State == "ready" && (await inbox.Inbox(null, null, 10)).Messages.Count == 3, "Did not resume after storage recovered");
        handler.Respond = _ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)); return limited;
        };
        Check((await Refresh()).State == "limited", "No actionable rate-limit status");
        requests = handler.Requests;
        Check((await Refresh()).State == "limited" && handler.Requests == requests, "Refresh ignored Retry-After");
        await graph.Disconnect(account.Id);
        Check((await inbox.Inbox(account.Id, null, 10)).Messages.Count == 0, "Disconnected identity exposed private remote summaries");
        Console.WriteLine("PASS unified Graph receive: bounded delta, equal-date paging, downloaded reply/forward, offline/readable MIME, renewed consent, Retry-After and transactional SQLITE_FULL recovery");
    }
}
