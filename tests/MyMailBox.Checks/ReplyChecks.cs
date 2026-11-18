using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class ComposerChecks
{
    public static async Task Replies()
    {
        var f=await FilesFixture();var d=f.Draft;
        f.Drafts.Configure(d.Account,new("",["orders@example.test"]));
        using var mime=new MimeMessage();mime.From.Add(MailboxAddress.Parse("author@example.test"));mime.ReplyTo.Add(MailboxAddress.Parse("team@example.test"));
        mime.To.Add(MailboxAddress.Parse("orders@example.test"));mime.To.Add(MailboxAddress.Parse("team@example.test"));
        mime.Cc.Add(MailboxAddress.Parse("peer@example.test"));mime.Bcc.Add(MailboxAddress.Parse("private@example.test"));
        mime.Subject="采购";mime.MessageId="child@example.test";mime.References.Add("root@example.test");
        var builder=new BodyBuilder{TextBody="报价单内容"};builder.Attachments.Add("附件.txt","报价"u8.ToArray());mime.Body=builder.ToMessageBody();
        using var data=new MemoryStream();await mime.WriteToAsync(data);var raw=data.ToArray();
        var source=await f.Store.Import(SqliteMailStore.Inbox,raw,await new MyMailBox.Mail.MimeReader().Parse(raw));
        MailChecks.Check(f.Drafts.SourceAccount(source.ToString())==SqliteMailStore.LocalAccount,"Original account not recovered");
        var reply=await f.Drafts.Respond(new(source.ToString(),ReplyMode.ReplyAll),d.Account);
        MailChecks.Check(reply.Identity=="orders@example.test"&&InternetAddressList.Parse(reply.To).Mailboxes.Single().Address=="team@example.test"&&
            InternetAddressList.Parse(reply.Cc).Mailboxes.Single().Address=="peer@example.test"&&reply.Bcc==""&&reply.Parts.Length==0&&
            reply.InReplyTo==mime.MessageId&&reply.References.SequenceEqual(new[]{"root@example.test","child@example.test"}),"Reply-All expanded blind/self recipients, repeated addresses or broke references");
        MailChecks.Check(reply.Message().Body.Contains("报价单内容")&&!(reply with{Quote=reply.Quote! with{Included=false}}).Message().Body.Contains("报价单内容"),"Quote toggle ignored");
        var forward=await f.Drafts.Respond(new(source.ToString(),ReplyMode.Forward),d.Account);
        MailChecks.Check(forward.To==""&&forward.Cc==""&&forward.Bcc==""&&forward.Parts.Single().Name=="附件.txt"&&forward.InReplyTo is null,"Forward recipient/attachment scope incorrect");
        await f.Service.Enqueue(reply.Id,d.Account,reply.Message());await f.Service.Enqueue(reply.Id,d.Account,reply.Message());
        var rows=await ((IOutboxService)f.Service).List(d.Account,0,25);MailChecks.Check(rows.Count==1,"Reply retry duplicated submission");
        using var db=f.Store.Open();using var c=db.CreateCommand();c.CommandText="SELECT raw FROM outbox WHERE id=$id";c.Parameters.AddWithValue("$id",reply.Id.ToString());
        using var outgoing=MimeMessage.Load(new MemoryStream((byte[])c.ExecuteScalar()!));
        MailChecks.Check(outgoing.InReplyTo==mime.MessageId&&outgoing.References.Count==2&&outgoing.Bcc.Count==0,"Frozen reply MIME lost ancestry or privacy");
        Console.WriteLine("PASS reply, all and forward identity/Reply-To, alias/self dedup, hidden recipients, ancestry, removable quote and forwarded attachment, idempotent submission");
    }
}
