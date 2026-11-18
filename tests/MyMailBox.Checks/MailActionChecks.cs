using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;

internal static class MailActionChecks
{
    public static async Task Run(bool live)
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/action-" + Guid.NewGuid().ToString("N")));
        var accounts = new AccountService(store);
        var service = new ProtocolMailService(store);
        IMailOrganizer organizer = service;
        var account = live ? await accounts.Save(new(null,"IMAP","imap","alice@localhost")) : await accounts.Save(new(null,"动作","local"));
        using var writer = new ImapClient();
        if (live)
        {
            var pin = File.ReadAllText(Path.Combine(PluginChecks.Repository,"artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
            await service.Save(account.Id,new(new("127.0.0.1",3993,"alice@localhost",CertificateSha256:pin)),"local-alice",null);
            writer.ServerCertificateValidationCallback = (_,cert,_,_) => cert?.GetCertHashString(HashAlgorithmName.SHA256)==pin;
            await writer.ConnectAsync("127.0.0.1",3993,SecureSocketOptions.SslOnConnect); await writer.AuthenticateAsync("alice@localhost","local-alice");
        }
        var prefix = "action-" + Guid.NewGuid().ToString("N");
        var source = await organizer.Create(account.Id,null,prefix+"-source");
        var target = await organizer.Create(account.Id,null,prefix+"-target");
        if (live)
        {
            var f = await writer.GetFolderAsync(prefix+"-source");
            using var mail = new MimeMessage(); mail.Subject="动作测试"; mail.Body=new TextPart("plain") { Text="完整正文" };
            await f.AppendAsync(mail); await f.AppendAsync(mail);
            await service.Sync(account.Id,f.FullName,50);
        }
        else
        {
            var raw = MailChecks.Sample(); var parsed = await new MyMailBox.Mail.MimeReader().Parse(raw);
            await store.Import(source,raw,parsed); await store.Import(source,raw,parsed);
        }
        var key = (await organizer.Messages(account.Id,source,0,20)).Items.First().Key;
        await organizer.Mark(account.Id,source,[key],MailMark.AddLabel,"保留");
        async Task<MailActionResult> Act(MailAction action,Guid from,string item,Guid? to = null)
        {
            var result=(await service.Apply(new(account.Id,from,[item],action,to))).Single();
            MailChecks.Check(result.Success,result.Detail);
            return result;
        }
        await Act(MailAction.Copy,source,key,target);
        var copies=await organizer.Messages(account.Id,target,0,20);
        MailChecks.Check(copies.Items.Count==1 && (await organizer.Messages(account.Id,source,0,20)).Items.Count==2,"Copy changed source");
        var moved=await Act(MailAction.Move,source,key,target);
        MailChecks.Check((await organizer.Messages(account.Id,target,0,20)).Items.Count==2 && (await organizer.Messages(account.Id,source,0,20)).Items.Count==1,"Move lost or duplicated mail");
        if (!live)
        {
            await service.Undo(moved.UndoId!.Value);
            var archived = await Act(MailAction.Archive,source,key);
            await service.Undo(archived.UndoId!.Value);
            var trash = await Act(MailAction.Trash,source,key);
            var bin = (await organizer.Folders(account.Id)).Single(f=>f.Role=="trash");
            MailChecks.Check((await organizer.Messages(account.Id,bin.Id,0,20)).Items.Single().Labels.Contains("保留"),"Local trash lost tags");
            await service.Undo(trash.UndoId!.Value);
            var invalid = (await service.Apply(new(account.Id,source,[key],MailAction.Move,Guid.NewGuid()))).Single();
            MailChecks.Check(!invalid.Success && (await store.Read(Guid.Parse(key))).Length>0,"Invalid destination lost source");
        }
        else
        {
            MailChecks.Check(moved.UndoId is null,"Offered remote undo without reliable mapping");
            Console.WriteLine("IMAP move path: "+moved.Detail);
            ProtocolMailService Without(ImapCapabilities capabilities) => new(store, () =>
            {
                var client = new ImapClient();
                client.Authenticated += (_, _) => client.Capabilities &= ~capabilities;
                return client;
            });
            var left = (await organizer.Messages(account.Id,source,0,20)).Items.Single();
            var request = new MailActionRequest(account.Id, source, [left.Key], MailAction.Move, target);
            var refused = (await Without(ImapCapabilities.Move | ImapCapabilities.UidPlus).Apply(request)).Single();
            MailChecks.Check(!refused.Success && (await organizer.Messages(account.Id,source,0,20)).Items.Count == 1,
                "Unsafe global expunge fallback allowed");
            var fallback = (await Without(ImapCapabilities.Move).Apply(request)).Single();
            MailChecks.Check(fallback.Success && fallback.Detail.Contains("验证目标") &&
                (await organizer.Messages(account.Id,source,0,20)).Items.Count == 0, "Verified COPYUID fallback failed: " + fallback.Detail);
        }
        var deleted=await Act(MailAction.DeletePermanently,target,copies.Items[0].Key);
        MailChecks.Check(deleted.UndoId is null && !(await organizer.Messages(account.Id,target,0,20)).Items.Any(m=>m.Key==copies.Items[0].Key),"Permanent removal failed");
        if (live) await writer.DisconnectAsync(true);
        Console.WriteLine("PASS same-store actions ("+(live?"GreenMail":"local")+"): copy, safe move, isolated permanent removal and capability-specific undo");
    }
}
