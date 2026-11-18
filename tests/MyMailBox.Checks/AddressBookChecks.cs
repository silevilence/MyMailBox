using MyMailBox.Contracts;
using MyMailBox.Runtime;
using MimeKit;

internal static partial class ComposerChecks
{
    public static async Task Addresses()
    {
        var f=await FilesFixture();var a=f.Draft.Account;var book=f.Drafts;
        book.Contact(a,new("采购甲","buyer@example.test"));
        var row=(await book.Suggest(a,"采购")).Single();
        MailChecks.Check(row.Contact && row.Address=="buyer@example.test" && (await book.Suggest(Guid.NewGuid(),"")).Count==0,"Address suggestion confused names/addresses or account scope");
        var fields=book.Normalize(new(""" "Buyer, Team" <buyer@example.test> ""","BUYER@example.test, cc@example.test","cc@example.test, blind@example.test"));
        MailChecks.Check(fields.Duplicates==2 && InternetAddressList.Parse(fields.To).Mailboxes.Single().Name=="Buyer, Team" &&
            fields.Cc=="cc@example.test" && fields.Bcc=="blind@example.test","To/Cc/Bcc dedup or display name lost");
        await PluginChecks.Reject(()=>Task.Run(()=>book.Normalize(new("","bad",""))),"Cc");
        book.Forget(a,null);MailChecks.Check((await book.Suggest(a,"")).Count==1,"Clearing history removed explicit contact");
        book.Contact(a,new("采购乙",row.Address));book.Forget(a,row.Address);MailChecks.Check((await book.Suggest(a,"")).Count==0,"Contact could not be edited/removed");
        var pages=new Contributions();await using var runtime=new PluginRuntime(pages);var ext=PluginChecks.Example();
        await runtime.Start(ext with{Manifest=ext.Manifest with{Entry="example-addresses",Permissions=["contacts.suggest"]}},new(){InstanceId="addresses",Grants=["contacts.suggest:"+a]});
        var provider=pages.AddressProviders.Single();using var stop=new CancellationTokenSource();var pending=provider.Suggest(a,"buyer",stop.Token);stop.Cancel();
        try{await pending;throw new Exception("Plugin ignored cancellation");}catch(OperationCanceledException){}
        MailChecks.Check((await provider.Suggest(a,"采购",default)).Count==1,"External address provider did not return names and addresses");
        await runtime.Stop("addresses");await PluginChecks.Reject(()=>provider.Suggest(a,"buyer",default),"停止接单");
        MailChecks.Check(book.Normalize(new("eml@example.test","","")).To=="eml@example.test","Manual entry depended on provider");
        Console.WriteLine("PASS local named contacts, account isolation, three recipient fields, dedup, correction, clear history and cancellable external provider");
    }
}
