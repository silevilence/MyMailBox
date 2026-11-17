using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class ProtocolChecks
{
    public static async Task Run()
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/protocol-" + Guid.NewGuid().ToString("N")));
        var accounts = new AccountService(store); var service = new ProtocolMailService(store);
        var account = await accounts.Save(new(null, "测试", "imap-smtp", "alice@localhost"));
        var settings = new ProtocolSettings(new("localhost", 3993, "alice@localhost"), new("localhost", 3465, "different-login"));
        await service.Save(account.Id, settings, "incoming-local-secret", "outgoing-local-secret");
        MailChecks.Check(await new ProtocolMailService(store).Settings(account.Id) == settings, "Protocol settings were not persisted independently");
        await service.Save(account.Id, settings, "", "");
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM credentials";
            MailChecks.Check(Convert.ToInt32(command.ExecuteScalar()) == 2, "Blank credentials replaced existing values");
            command.CommandText = "SELECT settings FROM protocol_settings";
            MailChecks.Check(!((string)command.ExecuteScalar()!).Contains("local-secret"), "Secret leaked into settings");
        }
        await Reject(() => service.Save(account.Id, settings with { Incoming = settings.Incoming! with { Port = 0 } }, null, null), "端口");
        await Reject(() => service.Save(account.Id, settings with { Incoming = settings.Incoming! with { CertificateSha256 = "invalid" } }, null, null), "指纹");
        var pop = await accounts.Save(new(null, "POP", "pop3", "bob@localhost"));
        await Reject(() => service.Folders(pop.Id), "不支持");
        await Reject(() => service.Send(pop.Id, new("alice@localhost", "", "", "test", "test")), "不支持");
        await Reject(() => service.Send(account.Id, new("bad address", "", "", "test", "test")), "地址");
        await Reject(() => service.Send(account.Id, new("", "", "", "test", "test")), "收件人");
        await Reject(() => service.Send(account.Id, new("bob@localhost", "", "", "bad\r\nBcc: bad@localhost", "test")), "主题");
        await accounts.Remove(account.Id);
        using (var db = store.Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM credentials";
            MailChecks.Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "Account removal leaked protocol credentials");
        }
        Console.WriteLine("PASS protocols: separate endpoint persistence, protected secrets, removal, capability and configuration validation");
    }
    private static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); } catch (InvalidDataException error) when (error.Message.Contains(message)) { return; }
        throw new Exception("Expected rejection: " + message);
    }
}
