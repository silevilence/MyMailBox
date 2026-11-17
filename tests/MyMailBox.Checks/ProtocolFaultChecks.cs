using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class ProtocolChecks
{
    public static async Task Faults()
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/protocol-faults-" + Guid.NewGuid().ToString("N")));
        var accounts = new AccountService(store); var service = new ProtocolMailService(store);
        var sender = await accounts.Save(new(null, "SMTP", "smtp", "alice@localhost"));
        await using (var smtp = new ProtocolFaultServer(true))
        {
            await service.Save(sender.Id, new(Outgoing: new("127.0.0.1", smtp.Port, "alice", CertificateSha256: smtp.Pin)), null, "test");
            var partial = await service.Send(sender.Id, new("bob@localhost,reject@localhost", "", "blind@localhost", "partial", "body"));
            MailChecks.Check(partial.State == "partial" && partial.Accepted.Count == 2 && partial.Rejected.SequenceEqual(new[] { "reject@localhost" }), "Partial recipients were misreported");
            MailChecks.Check(!smtp.Received!.Contains("blind@localhost") && !smtp.Received.Contains("Bcc:"), "Bcc leaked");
            smtp.RejectSender = true;
            MailChecks.Check((await service.Send(sender.Id, new("bob@localhost", "", "", "sender", "body"))).State == "rejected", "Unauthorized sender was reported sent");
            smtp.RejectSender = false;
            MailChecks.Check((await service.Send(sender.Id, new("reject@localhost", "", "", "reject", "body"))).State == "rejected", "All-recipient rejection was reported sent");
            smtp.DropAfterData = true;
            MailChecks.Check((await service.Send(sender.Id, new("bob@localhost", "", "", "unknown", "body"))).State == "unknown", "Lost DATA acknowledgement must be unknown");
        }
        var pop = await accounts.Save(new(null, "POP", "pop3", "bob@localhost"));
        await using (var server = new ProtocolFaultServer { DropSecondRetrieval = true })
        {
            await service.Save(pop.Id, new(new("127.0.0.1", server.Port, "bob", CertificateSha256: server.Pin)), "test", null);
            await Reject(() => service.ReceivePop(pop.Id, true), "连接");
            MailChecks.Check(server.Deletes == 0, "Interrupted POP session deleted sources without QUIT");
            var folder = (await store.Folders()).Single(f => f.AccountId == pop.Id);
            MailChecks.Check((await store.List(folder.Id, 0, 10)).Items.Count == 1, "First POP commit did not survive interruption");
            server.DropSecondRetrieval = false;
            var resumed = await new ProtocolMailService(store).ReceivePop(pop.Id, true);
            MailChecks.Check(resumed.Existing == 1 && resumed.Imported == 1 && server.Deletes == 2, "POP resume/restart lost or duplicated mail");
        }
        await using (var server = new ProtocolFaultServer { Uids = false })
        {
            await service.Save(pop.Id, new(new("127.0.0.1", server.Port, "bob", CertificateSha256: server.Pin)), "test", null);
            await Reject(() => service.ReceivePop(pop.Id, true), "UIDL");
            var first = await service.ReceivePop(pop.Id, false); var second = await service.ReceivePop(pop.Id, false);
            MailChecks.Check(first.Imported == 2 && second.Imported == 0 && second.Existing == 2 && server.Deletes == 0, "No-UIDL safe fallback failed");
        }
        await using (var server = new ProtocolFaultServer())
        {
            await service.Save(pop.Id, new(new("127.0.0.1", server.Port, "bob", CertificateSha256: server.Pin)), "test", null);
            using (var db = store.Open()) { using var command = db.CreateCommand(); command.CommandText = "CREATE TRIGGER fail_pop_write BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT,'injected disk failure'); END;"; command.ExecuteNonQuery(); }
            try { await service.ReceivePop(pop.Id, true); throw new Exception("Expected storage failure"); } catch (Microsoft.Data.Sqlite.SqliteException) { }
            MailChecks.Check(server.Deletes == 0, "Storage failure deleted source");
            using (var db = store.Open()) { using var command = db.CreateCommand(); command.CommandText = "DROP TRIGGER fail_pop_write;"; command.ExecuteNonQuery(); }
            MailChecks.Check((await service.ReceivePop(pop.Id, false)).Imported == 2, "Storage failure committed a false receipt");
            using (var db = store.Open()) { using var command = db.CreateCommand(); command.CommandText = "UPDATE messages SET sha256='broken' WHERE folder_id IN (SELECT id FROM folders WHERE account_id=$id)"; command.Parameters.AddWithValue("$id", pop.Id.ToString()); command.ExecuteNonQuery(); }
            await Reject(() => service.ReceivePop(pop.Id, true), "校验"); MailChecks.Check(server.Deletes == 0, "Corrupt local copy deleted source");
        }
        Console.WriteLine("PASS protocol faults: partial/rejected/unknown SMTP, Bcc privacy, interrupted POP+restart, no UIDL, atomic storage failure and corrupt-copy source protection");
    }
}
