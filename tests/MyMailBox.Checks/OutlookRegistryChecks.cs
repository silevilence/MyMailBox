using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using MyMailBox.Contracts;
using MyMailBox.Mail;

[SupportedOSPlatform("windows")]
internal static class OutlookRegistryChecks
{
    [DllImport("advapi32.dll")]
    private static extern int RegOverridePredefKey(nint original, nint replacement);

    // Isolated CLI group. HKCU is redirected in this process only while the importer reads the fixture.
    public static async Task Run()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "registry-import");
        var store = new SqliteMailStore(path);
        var accounts = new AccountService(store);
        var importer = new OutlookAccountImport(accounts);
        var mail = new ProtocolMailService(store);
        await using var smtp = new ProtocolFaultServer(true) { AcceptAll = true };
        MailAccount imported;
        ProtocolSettings settings;
        string scratch = @"Software\MyMailBox-fixture-" + Guid.NewGuid().ToString("N");
        using var original = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        try
        {
            using var fixture = original.CreateSubKey(scratch);
            using (var row = fixture.CreateSubKey(@"Software\Microsoft\Office\16.0\Outlook\Profiles\本地样本\9375CFF0413111d3B88A00104B2A6676\00000001"))
            {
                row.SetValue("Account Name", "样本发件账户");
                row.SetValue("Email", System.Text.Encoding.Unicode.GetBytes("test@example.invalid\0"));
                row.SetValue("IMAP Server", "127.0.0.1"); row.SetValue("IMAP User", "fixture"); row.SetValue("IMAP Port", 3993);
                row.SetValue("SMTP Server", "127.0.0.1"); row.SetValue("SMTP User", "fixture"); row.SetValue("SMTP Port", BitConverter.GetBytes(smtp.Port));
            }
            // HKEY_CURRENT_USER is a sign-extended predefined Win32 handle.
            var hkcu = (nint)unchecked((int)0x80000001);
            var result = RegOverridePredefKey(hkcu, fixture.Handle.DangerousGetHandle());
            if (result != 0) throw new System.ComponentModel.Win32Exception(result);
            try
            {
                var profiles = await importer.Profiles(default);
                MailChecks.Check(profiles.SequenceEqual(["本地样本"]), "Fixture hive not isolated");
                var entry = (await importer.Preview(profiles.Single(), default)).Single();
                MailChecks.Check(entry.Outgoing!.Port == smtp.Port && entry.Incoming!.User == "fixture", "Registry field decoding failed");
                settings = new(new(entry.Incoming!.Host, entry.Incoming.Port!.Value, entry.Incoming.User),
                    new(entry.Outgoing.Host, entry.Outgoing.Port!.Value, entry.Outgoing.User, CertificateSha256:smtp.Pin));
                imported = await importer.ImportSource(profiles.Single(),entry,new(null,entry.Name,entry.Provider,entry.Address),settings,null,default);
                var reopened = new OutlookAccountImport(new AccountService(new SqliteMailStore(path)));
                MailChecks.Check((await reopened.Preview(profiles.Single(),default)).Single().ImportedAccount == imported.Id, "Lost registry import receipt");
            }
            finally
            {
                result = RegOverridePredefKey(hkcu, 0);
                if (result != 0) throw new System.ComponentModel.Win32Exception(result);
            }
        }
        finally { original.DeleteSubKeyTree(scratch, false); }
        await mail.Save(imported.Id, settings, null, "local-fixture");
        var delivery = await mail.Send(imported.Id,new("recipient@example.invalid","","","注册表样本收发","导入账户稳定引用"));
        MailChecks.Check(delivery.State=="sent" && smtp.Submissions==1 && smtp.RcptCommands.Count==1, "Imported registry account failed SMTP");
        Console.WriteLine("PASS classic Outlook registry fixture: process-local HKCU, binary fields, account and receipt reopened, separate credential, one real loopback TLS SMTP delivery");
    }
}
