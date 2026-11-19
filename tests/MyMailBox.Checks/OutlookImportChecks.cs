using System.Text;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class OutlookImportChecks
{
    public static async Task Run()
    {
        var store = new SqliteMailStore(Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "outlook-import"));
        var accounts = new AccountService(store);
        var protocols = new ProtocolMailService(store);
        var importer = new OutlookAccountImport(accounts);
        var fields = new Dictionary<string, object>
        {
            ["Account Name"] = "迁移演示", ["Email"] = Encoding.Unicode.GetBytes("one@example.invalid\0"),
            ["IMAP Server"] = "in.example.invalid", ["IMAP Port"] = BitConverter.GetBytes(993), ["IMAP User"] = "one",
            ["SMTP Server"] = "out.example.invalid", ["SMTP Port"] = 465, ["SMTP User"] = "sender"
        };
        var preview = OutlookAccountImport.ReadSection("Test profile", key =>
        {
            if (key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Requested a secret");
            return fields.GetValueOrDefault(key);
        })!;
        MailChecks.Check(preview.Address == "one@example.invalid" && preview.Provider == "imap-smtp" && preview.Incoming?.Port == 993, "Incorrect Outlook profile preview");
        var edit = new AccountEdit(null, "导入名称", preview.Provider, preview.Address, Tags: ["迁移"]);
        var settings = new ProtocolSettings(new(preview.Incoming!.Host, 993, preview.Incoming.User), new(preview.Outgoing!.Host, 465, preview.Outgoing.User));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await importer.Import(edit, settings, null, cancelled.Token); throw new Exception("Accepted cancelled import"); } catch (OperationCanceledException) { }
        MailChecks.Check(!(await importer.Matches(edit.Provider, edit.Address, default)).Any(), "Preview or cancellation created account");
        try { await importer.Import(edit, settings with { Incoming = null }, null, default); throw new Exception("Accepted incomplete settings"); } catch (InvalidDataException) { }
        var account = await importer.Import(edit, settings, null, default);
        MailChecks.Check(await protocols.Settings(account.Id) == settings && account.Tags.SequenceEqual(["迁移"]), "Did not commit account and settings together");
        try { await importer.Import(edit, settings, null, default); throw new Exception("Duplicated same account"); } catch (InvalidDataException) { }
        var mapped = await importer.Import(edit with { Name = "other" }, new(), account.Id, default);
        MailChecks.Check(mapped.Id == account.Id && mapped.Name == account.Name && (await importer.Matches(edit.Provider, edit.Address, default)).Count == 1, "Duplicate mapping changed account");
        var next = preview with { Source = "fixture / 2", Address = "two@example.invalid", Name = "第二个账户" };
        var rows = new[] { preview, next };
        OutlookAccountImport Open() => new(new AccountService(store), (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<OutlookAccountPreview>>(rows); });
        var migration = Open();
        var recorded = await migration.ImportSource("fixture", preview, edit, new(), account.Id, default);
        migration = Open();
        var afterRestart = await migration.Preview("fixture", default);
        MailChecks.Check(afterRestart[0].ImportedAccount == recorded.Id && afterRestart[1].ImportedAccount == null, "Restart lost per-section progress");
        rows[1] = next with { Incoming = new("changed.example.invalid", 993, "two") };
        await PluginChecks.Reject(() => migration.ImportSource("fixture", next, edit with { Address = next.Address }, settings, null, default), "重新读取");
        var fresh = (await migration.Preview("fixture", default))[1];
        var nextEdit = edit with { Address = fresh.Address };
        var newSettings = settings with { Incoming = new(fresh.Incoming!.Host, 993, fresh.Incoming.User) };
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => migration.ImportSource("fixture", fresh, nextEdit, newSettings, null, default)));
        MailChecks.Check(results[0].Id == results[1].Id && (await migration.Matches(nextEdit.Provider, nextEdit.Address, default)).Count == 1, "Concurrent replay duplicated imported account");
        rows[1] = next;
        var changed = (await Open().Preview("fixture", default))[1];
        MailChecks.Check(changed.SourceChanged && changed.ImportedAccount == null, "Changed source retained previous confirmation");
        await migration.ImportSource("fixture", changed, nextEdit, new(), results[0].Id, default);
        MailChecks.Check((await Open().Preview("fixture", default)).All(x => x.ImportedAccount != null) && await protocols.Settings(results[0].Id) == newSettings,
            "Reconfirmed mapping replaced the target settings");
        using var db = store.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM credentials";
        MailChecks.Check(Convert.ToInt32(cmd.ExecuteScalar()) == 0, "Import moved credentials");
        Console.WriteLine("PASS Outlook configuration fixture: whitelisted fields, source preview, atomic save, reauthorization, cancellation and stable duplicate mapping");
    }
}
