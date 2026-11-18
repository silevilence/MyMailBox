using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class ProtocolChecks
{
    public static async Task Outbox()
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/outbox-" + Guid.NewGuid().ToString("N")));
        var service = new ProtocolMailService(store);
        var account = await new AccountService(store).Save(new(null, "发件箱验收", "smtp", "sender@localhost"));
        await using var smtp = new ProtocolFaultServer(true);
        var settings = new ProtocolSettings(Outgoing: new("127.0.0.1", smtp.Port, "sender", CertificateSha256: smtp.Pin));
        await service.Save(account.Id, settings, null, "local-test");
        var mail = new TextMail("accepted@localhost,reject@localhost", "", "blind@localhost", "测试", "正文");
        var id = Guid.NewGuid();
        await service.Enqueue(id, account.Id, mail);
        await service.Enqueue(id, account.Id, mail);
        await Reject(() => service.Enqueue(id, account.Id, mail with { Body = "different" }), "另一封");
        MailChecks.Check((await service.List(account.Id, 0, 20)).Count == 1 && smtp.Submissions == 0, "Enqueue was not durable and idempotent");
        var partial = await service.Deliver(id);
        MailChecks.Check(partial.State == "partial" && partial.Accepted.Count == 2, "Partial submission not recorded");
        var resumed = new ProtocolMailService(store);
        smtp.AcceptAll = true;
        smtp.RcptCommands.Clear();
        await resumed.Retry(id);
        var result = await resumed.Deliver(id);
        MailChecks.Check(result.State == "sent" && result.Accepted.Count == 3 && smtp.RcptCommands.Count == 1 &&
            smtp.RcptCommands[0].Contains("reject@localhost"), "Retry redelivered accepted recipients");
        int count = smtp.Submissions;
        await resumed.Deliver(id);
        MailChecks.Check(smtp.Submissions == count, "Completed item submitted again");

        var unknown = Guid.NewGuid();
        await service.Enqueue(unknown, account.Id, mail);
        smtp.DropAfterData = true;
        MailChecks.Check((await service.Deliver(unknown)).State == "unknown", "Lost DATA receipt not marked uncertain");
        await Reject(() => resumed.Retry(unknown), "不能安全重试");
        int afterDrop = smtp.Submissions;
        await resumed.Deliver(unknown);
        MailChecks.Check(smtp.Submissions == afterDrop, "Uncertain submission retried");
        smtp.DropAfterData = false;

        var canceled = Guid.NewGuid();
        await service.Enqueue(canceled, account.Id, mail);
        await resumed.Cancel(canceled);
        MailChecks.Check((await resumed.Deliver(canceled)).State == "canceled", "Cancel did not survive reopening");

        void Sql(string sql)
        {
            using var db = store.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        var interrupted = Guid.NewGuid();
        await service.Enqueue(interrupted, account.Id, mail);
        Sql("UPDATE outbox SET state='sending' WHERE state='pending'");
        MailChecks.Check((await resumed.Deliver(interrupted)).State == "unknown" && smtp.Submissions == afterDrop, "Process-loss recovery resent mail");

        var queued = Guid.NewGuid();
        await service.Enqueue(queued, account.Id, mail);
        await service.Save(account.Id, new(Outgoing: settings.Outgoing! with { Port = 1 }), null, null);
        MailChecks.Check((await service.Deliver(queued)).State == "pending", "Offline submission not retained");
        await service.Save(account.Id, settings, null, null);
        await service.Retry(queued);
        Sql("CREATE TRIGGER fail_copy BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT,'fixture'); END");
        using (new OutboxRunner(new ProtocolMailService(store)))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!(await service.List(account.Id, 0, 20)).Single(i => i.Id == queued).CopyPending)
                await Task.Delay(50, timeout.Token);
        }
        var delivered = (await service.List(account.Id, 0, 20)).Single(i => i.Id == queued);
        MailChecks.Check(delivered.State == "sent" && delivered.CopyPending, "Copy failure erased delivery receipt");
        int beforeCopy = smtp.Submissions;
        Sql("DROP TRIGGER fail_copy");
        await resumed.SaveCopy(queued);
        await resumed.SaveCopy(queued);
        MailChecks.Check(smtp.Submissions == beforeCopy && !(await resumed.List(account.Id, 0, 20)).Single(i => i.Id == queued).CopyPending, "Copy retry resubmitted SMTP");
        var folder = (await store.Folders()).Single(f => f.Role == "local-sent-copy");
        MailChecks.Check((await store.List(folder.Id, 0, 20)).Items.Count == 2, "Local sent copy duplicated");
        MailChecks.Check((await service.History(queued)).Any(s => s.Contains("未连接")), "Attempt history lost");
        Console.WriteLine("PASS outbox: idempotent queue, partial retry envelope, lost acknowledgement, canceled and interrupted restart, offline recovery and local-only copy retry");
    }
}
