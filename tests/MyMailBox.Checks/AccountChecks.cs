using System.Text;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class AccountChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyMailBox-accounts-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteMailStore(directory); var accounts = new AccountService(store);
        var edit = new AccountEdit(null, "工作", "imap-smtp", "user@example.test", "work", ["工作", "工作"], Notes: "备注");
        var secret = "test-only-secret-" + Guid.NewGuid();
        var original = await accounts.Save(edit, secret);
        MailChecks.Check(original.Id != Guid.Empty && original.Tags.Length == 1, "Stable ID/tags missing");
        var renamed = await accounts.Save(edit with { Id = original.Id, Name = "改名" });
        MailChecks.Check(renamed.Id == original.Id && renamed.CredentialReference == original.CredentialReference, "Rename changed reference");
        await Reject(() => accounts.Save(edit with { Alias = "WORK" }), "alias");
        var receiveOnly = await accounts.Save(edit with { Alias = "receive", Capabilities = AccountCapabilities.Receive });
        var sendOnly = await accounts.Save(edit with { Alias = "send", Provider = "smtp" });
        MailChecks.Check(receiveOnly.Capabilities.HasFlag(AccountCapabilities.Receive) && !receiveOnly.Capabilities.HasFlag(AccountCapabilities.Send), "Receive-only mismatch");
        MailChecks.Check(sendOnly.Capabilities == AccountCapabilities.Send, "Send-only mismatch");
        var local = await accounts.Save(new(null, "独立归档", "local", Alias: "archive"));
        MailChecks.Check(!local.Capabilities.HasFlag(AccountCapabilities.Send), "Local can send");
        var folder = (await store.Folders()).Single(f => f.AccountId == local.Id);
        var raw = MailChecks.Sample(); var id = await store.Import(folder.Id, raw, await new MimeReader().Parse(raw));
        var reopened = new AccountService(new SqliteMailStore(directory));
        MailChecks.Check(await reopened.ReadCredential(original.Id) == secret, "Protected credential did not survive reopen");
        using (var db = store.Open())
        {
            using var query = db.CreateCommand(); query.CommandText = "SELECT protected FROM credentials WHERE id=$id"; query.Parameters.AddWithValue("$id", original.CredentialReference!);
            var encrypted = (byte[])query.ExecuteScalar()!;
            MailChecks.Check(!Encoding.UTF8.GetString(encrypted).Contains(secret), "Credential stored in plain text");
            query.CommandText = "SELECT credential_ref FROM accounts WHERE id=$account"; query.Parameters.AddWithValue("$account", original.Id.ToString());
            MailChecks.Check(query.ExecuteScalar() as string == original.CredentialReference, "Account does not contain only reference");
        }
        await accounts.AuthenticationFailed(original.Id);
        MailChecks.Check((await accounts.List()).Single(a => a.Id == original.Id).AuthenticationState.Contains("失败"), "Auth failure not visible");
        var repaired = await accounts.Save(edit with { Id = original.Id }, "replacement");
        MailChecks.Check(!repaired.AuthenticationState.Contains("失败") && await accounts.ReadCredential(original.Id) == "replacement", "Auth recovery failed");
        await accounts.Save(edit with { Id = original.Id, Enabled = false });
        MailChecks.Check(await accounts.ReadCredential(original.Id) is null, "Disabled account exposes credential to provider");
        await Reject(() => accounts.Restore(original), "ID");
        await Reject(() => accounts.Restore(original with { Id = Guid.NewGuid() }), "alias");
        var restored = await accounts.Restore(original with { Id = Guid.NewGuid(), Alias = "restored" });
        MailChecks.Check(restored.CredentialReference is null && restored.AuthenticationState.Contains("需要授权"), "Restore retained secret");
        await accounts.Remove(original.Id); await accounts.Remove(local.Id);
        MailChecks.Check(!(await accounts.List()).Any(a => a.Id == original.Id || a.Id == local.Id), "Removed account listed");
        MailChecks.Check((await store.Read(id)).SequenceEqual(raw), "Removal destroyed unique archive");
        MailChecks.Check(await accounts.ReadCredential(original.Id) is null, "Removed credential remains available");
        MailChecks.Check(!(await new AccountService(store).List()).Any(a => a.Id == local.Id), "Removed account returned on restart");
        Console.WriteLine("PASS accounts: stable identity, alias conflicts, same-type accounts, capabilities, DPAPI/reopen, auth recovery, restore conflicts, removal preserves archive");
    }
    private static async Task Reject(Func<Task<MailAccount>> action, string expected)
    {
        try { await action(); } catch (InvalidDataException error) when (error.Message.Contains(expected)) { return; }
        throw new Exception("Expected rejection: " + expected);
    }
}
