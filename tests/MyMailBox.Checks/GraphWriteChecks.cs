using System.Net;
using System.Text;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class GraphChecks
{
    public static async Task Writes()
    {
        var store = new SqliteMailStore(Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "graph-write"));
        var accounts = new AccountService(store);
        var account = await accounts.Save(new(null, "Graph 写信", "outlook-com", "writer@example.invalid"));
        var auth = new Authorization(new(store)) { Username = account.Address };
        var handler = new WriteHandler();
        using var http = new HttpClient(handler);
        using var graph = new GraphMailService(store, auth, http, Guid.NewGuid().ToString());
        var outbox = new ProtocolMailService(store) { Graph = graph };
        await graph.SignIn(account.Id);
        await Reject(() => graph.MarkRead(account.Id, "old", true, default), "授权");
        Check(handler.Calls == 0, "Write before consent");
        await graph.Enable(account.Id, default);
        Check((await graph.Settings(account.Id)).CanWrite, "Write grant not saved");
        var raw = "From: author@example.invalid\r\nTo: writer@example.invalid\r\nMessage-ID: <original@example.invalid>\r\nSubject: Original\r\n\r\nOriginal body"u8.ToArray();
        handler.Next = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };
        var localId = await graph.Download(account.Id, "old");
        handler.Next = r => r.Method == HttpMethod.Patch && r.RequestUri!.AbsolutePath.EndsWith("/messages/old") ? Json("""{"isRead":true}""") : throw new Exception("Wrong mark method");
        await graph.MarkRead(account.Id, "old", true, default);
        handler.Next = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/messages/old/move") && r.Content!.ReadAsStringAsync().Result.Contains("target-folder")
            ? new(HttpStatusCode.Created) {Content=new StringContent("""{"id":"moved"}""")} : throw new Exception("Wrong move");
        await graph.Move(account.Id, "old", "target-folder", default);
        var count = handler.Calls;
        Check(localId == await graph.Download(account.Id, "moved") && handler.Calls == count, "Move invalidated local identity");
        var drafts = new MailDraftStore(store);
        var reply = await drafts.Respond(new(localId.ToString(), ReplyMode.Reply), account.Id);
        var part = await drafts.AddPart(reply.Id, account.Id, "sample.bin", new MemoryStream([1,2,3]), false);
        reply = drafts.Save(reply with { Bcc = "blind@example.invalid", Parts = [part] });
        void VerifyMime(HttpRequestMessage r)
        {
            Check(r.Method == HttpMethod.Post && r.Content?.Headers.ContentType?.MediaType == "text/plain", "Expected MIME POST");
            using var mail = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(r.Content!.ReadAsStringAsync().Result)));
            var file = mail.Attachments.OfType<MimePart>().Single();
            using var bytes = new MemoryStream(); file.Content!.DecodeTo(bytes);
            Check(mail.InReplyTo == "original@example.invalid" && mail.Bcc.Mailboxes.Single().Address == "blind@example.invalid" && mail.To.Mailboxes.Single().Address == "author@example.invalid" && bytes.ToArray().SequenceEqual(new byte[]{1,2,3}), "Lost reply/Bcc/attachment");
        }
        handler.Next = r => { VerifyMime(r); Check(r.RequestUri!.AbsolutePath.EndsWith("/me/messages"), "Draft URI"); return new(HttpStatusCode.Created){Content=new StringContent("""{"id":"draft"}""")}; };
        Check(await graph.SaveDraft(reply, default) == "draft", "Missing cloud draft");
        count=handler.Calls; Check(await graph.SaveDraft(reply, default) == "draft" && handler.Calls == count, "Duplicate cloud draft");
        handler.Next = r => { VerifyMime(r); Check(r.RequestUri!.AbsolutePath.EndsWith("/me/sendMail"), "Send URI"); return new(HttpStatusCode.Accepted); };
        await outbox.Enqueue(reply.Id, account.Id, reply.Message());
        Check((await outbox.Deliver(reply.Id)).State == "sent", "Outbox Graph submission");
        count=handler.Calls; await outbox.Deliver(reply.Id); Check(handler.Calls==count, "Duplicate Graph submission");
        handler.Next = _ => throw new HttpRequestException("lost response");
        var uncertain=Guid.NewGuid(); await outbox.Enqueue(uncertain, account.Id, reply.Message());
        Check((await outbox.Deliver(uncertain)).State=="unknown", "Lost confirmation treated as a refusal");
        await Reject(() => outbox.Retry(uncertain), "安全重试");
        var largeId = Guid.NewGuid();
        var large = await drafts.AddPart(largeId, account.Id, "large.bin", new MemoryStream(new byte[3*1024*1024]), false);
        await Reject(() => outbox.Enqueue(largeId, account.Id, reply.Message() with { PartIds = [large.Id] }), "4 MiB");
        await graph.Disconnect(account.Id);
        Check(!(await accounts.List()).Single(a=>a.Id==account.Id).Capabilities.HasFlag(AccountCapabilities.Send), "Sending still enabled after disconnect");
        Console.WriteLine("PASS Graph write fixture: incremental consent, MIME draft and reply with Bcc/attachment, bounded payload, accepted vs unknown submission, move mapping and disconnect");
    }

    private sealed class WriteHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage,HttpResponseMessage> Next = _ => throw new Exception("Unexpected Graph request");
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri?.Host == "graph.microsoft.com" && request.Headers.Contains("Prefer"), "Invalid Graph host or immutable ID preference");
            Calls++; return Task.FromResult(Next(request));
        }
    }
}
