using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Text;

internal static partial class SearchChecks
{
    private static async Task<(SqliteMailStore Store, ProtocolMailService Protocols, MailSearchService Search, Guid Hit)> Fixture()
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/search-" + Guid.NewGuid().ToString("N")));
        var protocols = new ProtocolMailService(store);
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("author@example.test")); message.Sender = MailboxAddress.Parse("agent@example.test");
        message.To.Add(MailboxAddress.Parse("primary@example.test")); message.Cc.Add(MailboxAddress.Parse("copy@example.test"));
        message.Bcc.Add(MailboxAddress.Parse("visible@example.test")); message.ReplyTo.Add(MailboxAddress.Parse("reply@example.test"));
        var day = new DateOnly(2025, 4, 2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        message.Date = new DateTimeOffset(day); message.Headers.Add(HeaderId.Received, "from fixture.example.test; " + MimeKit.Utils.DateUtils.FormatDate(day.AddDays(1)));
        message.Headers.Add("X-Project", "采购甲"); message.Subject = "采购合同"; message.References.Add("root@example.test");
        message.Importance = MessageImportance.High;
        var content = new BodyBuilder { TextBody = "第一笔采购合同。第二行记录报价。" }; content.Attachments.Add("报价单.txt", Encoding.UTF8.GetBytes("文件"));
        message.Body = content.ToMessageBody(); using var buffer = new MemoryStream(); await message.WriteToAsync(buffer);
        var raw = buffer.ToArray(); var parser = new MyMailBox.Mail.MimeReader();
        var hit = await store.Import(SqliteMailStore.Inbox, raw, await parser.Parse(raw));
        message.Headers.Remove(HeaderId.Bcc); message.Date = day.AddSeconds(-1); message.Subject = "旧邮件"; message.Body = new TextPart("plain") { Text = "普通正文" };
        buffer.SetLength(0); await message.WriteToAsync(buffer); raw = buffer.ToArray();
        await store.Import(SqliteMailStore.Inbox, raw, await parser.Parse(raw));
        await protocols.Mark(SqliteMailStore.LocalAccount, SqliteMailStore.Inbox, [hit.ToString()], MailMark.Star, "");
        await protocols.Mark(SqliteMailStore.LocalAccount, SqliteMailStore.Inbox, [hit.ToString()], MailMark.AddLabel, "待办");
        return (store, protocols, new MailSearchService(store, protocols), hit);
    }

    public static async Task Fields()
    {
        var f = await Fixture();
        async Task<SearchPage> Find(string field, string value, SearchMatch match = SearchMatch.Contains) =>
            await f.Search.Search(new([new(field, match, value)]));
        foreach (var (field, value) in new[]{("from","AUTHOR@"),("sender","agent@"),("reply","reply@"),("to","primary@"),("cc","copy@"),
            ("bcc","visible@"),("attachment","报价单"),("header","X-Project: 采购甲"),("priority","high"),("thread","root@example.test"),("tag","待办")})
            MailChecks.Check((await Find(field, value)).Items.Any(h => h.LocalId == f.Hit), "Missing search field " + field);
        MailChecks.Check((await Find("bcc", "visible@")).Items.Count == 1 && (await Find("bcc", "primary@")).Items.Count == 0, "Inferred hidden Bcc");
        var on = await Find("sent", "2025-04-02", SearchMatch.Equals);
        MailChecks.Check(on.Items.Single().LocalId == f.Hit && (await Find("sent", "2025-04-02", SearchMatch.Before)).Items.Single().Subject == "旧邮件",
            "Local midnight boundary incorrect");
        MailChecks.Check((await Find("received", "2025-04-03", SearchMatch.Equals)).Items.Count == 2, "Receipt date confused with sent date");
        var starred = await Find("star", "是", SearchMatch.Equals);
        MailChecks.Check(starred.Items.Single().LocalId == f.Hit && starred.FullMessages == 2 && starred.Summaries == 0, "Flag/coverage incorrect");
        var hit = starred.Items[0]; MailChecks.Check(await f.Search.Position(hit, SearchOrder.Newest) == 0 && (await f.Search.Read(hit)).Contains("采购合同"), "Wrong exact result opened");
        var page = await f.Search.Search(new([], SearchOrder.Newest, 0, 1));
        var next = await f.Search.Search(new([], SearchOrder.Newest, 1, 1));
        MailChecks.Check(page.HasMore && !next.HasMore && next.Items[0].Id != page.Items[0].Id, "Search paging repeated mail");
        MailChecks.Check((await Find("subject", "' OR 1=1 --")).Items.Count == 0, "Literal treated as query");
        Console.WriteLine("PASS search fields: visible headers/addresses, attachment, thread, priority, flags, timezone boundaries, coverage, stable location and paging");
    }
}
