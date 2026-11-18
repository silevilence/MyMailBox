using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static class OrganizationChecks
{
    public static async Task Run(bool live)
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/organize-" + Guid.NewGuid().ToString("N")));
        var accounts = new AccountService(store);
        var protocols = new ProtocolMailService(store);
        IMailOrganizer service = protocols;
        var account = live ? await accounts.Save(new(null,"IMAP","imap","alice@localhost")) :
            await accounts.Save(new(null,"本地归档","local"));
        using var imap = new ImapClient();
        if (live)
        {
            var pin = File.ReadAllText(Path.Combine(PluginChecks.Repository,"artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
            await protocols.Save(account.Id,new(new("127.0.0.1",3993,"alice@localhost",CertificateSha256:pin)),"local-alice",null);
            imap.ServerCertificateValidationCallback = (_,cert,_,_) => cert?.GetCertHashString(HashAlgorithmName.SHA256)==pin;
            await imap.ConnectAsync("127.0.0.1",3993,SecureSocketOptions.SslOnConnect);
            await imap.AuthenticateAsync("alice@localhost","local-alice");
        }
        var name = "验收-" + Guid.NewGuid().ToString("N");
        var root = await service.Create(account.Id,null,name);
        var child = await service.Create(account.Id,root,"子目录");
        var selections = new SelectionService(store,accounts);
        var reference = new SelectionReference(SelectionKind.Folder,account.Id,child);
        var saved = new SavedSelection(false,[],[reference],SelectionKind.Folder,AccountCapabilities.Folders);
        await selections.Save("test-folder",saved);
        await service.Rename(account.Id,root,name+"-新");
        var folders = await service.Folders(account.Id);
        var renamed = folders.Single(f => f.Id == child);
        MailChecks.Check(renamed.Path.Contains("-新") && (await selections.Resolve(saved)).Missing.Length == 0,"Renamed child lost stable reference");
        await service.DeleteEmpty(account.Id,child);
        var replacement = await service.Create(account.Id,root,"子目录");
        MailChecks.Check(replacement != child && (await selections.Resolve(saved)).Missing.SequenceEqual(new[]{reference}),"Deleted reference rebound to same name");
        if (live)
        {
            var folder = await imap.GetFolderAsync((await service.Folders(account.Id)).Single(f => f.Id==replacement).Path);
            using var mail = new MimeMessage(); mail.Subject = "整理测试"; mail.Body = new TextPart("plain") { Text = "离线整理" };
            await folder.AppendAsync(mail); await folder.AppendAsync(mail);
        }
        else
        {
            var raw = MailChecks.Sample(); var parsed = await new MyMailBox.Mail.MimeReader().Parse(raw);
            await store.Import(replacement,raw,parsed); await store.Import(replacement,raw,parsed);
        }
        var page = await service.Messages(account.Id,replacement,0,20);
        var keys = page.Items.Select(m => m.Key).ToArray();
        var starred = await service.Mark(account.Id,replacement,[..keys,"0"],MailMark.Star,"");
        MailChecks.Check(starred.Count(r=>r.Success)==2 && !starred[^1].Success,"Bulk results did not distinguish missing mail");
        await service.Mark(account.Id,replacement,keys,MailMark.Read,"");
        await service.Mark(account.Id,replacement,keys,MailMark.AddLabel,"待办");
        var changed = await ((IMailOrganizer)new ProtocolMailService(store)).Messages(account.Id,replacement,0,20);
        MailChecks.Check(changed.Items.Count == 2 && changed.Items.All(m => m.Starred && m.Read && m.Labels.SequenceEqual(new[]{"待办"})), "Flags or local tags lost after reopening");
        if (live)
        {
            var f = await imap.GetFolderAsync((await service.Folders(account.Id)).Single(m=>m.Id==replacement).Path);
            await f.OpenAsync(FolderAccess.ReadOnly);
            var check = await f.FetchAsync(0,-1,MessageSummaryItems.Flags);
            MailChecks.Check(check.All(m=>m.Flags!.Value.HasFlag(MessageFlags.Flagged | MessageFlags.Seen) && !m.Keywords.Contains("待办")), "Local label leaked or flags not on server");
            await imap.DisconnectAsync(true);
        }
        await service.Mark(account.Id,replacement,keys,MailMark.Unstar,"");
        await service.Mark(account.Id,replacement,keys,MailMark.RemoveLabel,"待办");
        var last = await service.Messages(account.Id,replacement,0,20);
        MailChecks.Check(last.Items.All(m=>!m.Starred && m.Labels.Length==0 && m.Read),"Clearing flags altered unrelated metadata");
        Console.WriteLine("PASS organization (" + (live ? "GreenMail" : "local") + "): hierarchy rename, stable references, same-name recreation, batch results, read/star and separate local labels");
    }
}
