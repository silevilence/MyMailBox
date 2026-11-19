using System.Net;
using System.Text;
using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Mail.ActiveSync;

internal static class ExchangeChecks
{
    // Independent WBXML wire captures constructed from the published code-page tables.
    private static readonly Dictionary<string,string> Frames = new()
    {
        ["hierarchy"] = "03016A000007564C033100015203483100014E4F4703E694B6E4BBB6E7AEB100014803626F78000149033000014A03320001014F4703E5BD92E6A1A30001480361726368697665000149033000014A0331320001010101",
        ["withDrafts"] = "03016A000007564C033100015203483300014E4F470344726166747300014803647261667473000149033000014A03330001010101",
        ["draftPrime"] = "03016A00455C4F520364726166747300014E033100014B0344310001010101",
        ["draftSaved"] = "03016A00455C4F520364726166747300014E033100014B034432000146474C03" + "434C49454E54" + "00014D0364726166743100014E033100010101010101",
        ["hierarchyNoChange"] = "03016A000007564C0331000152034832000101",
        ["prime"] = "03016A00455C4F5203626F7800014E033100014B034B310001010101",
        ["first"] = "03016A00455C4F5203626F7800014E033100014B034B3200011456474D036D3100015D00025403E4B8ADE69687E4B8BBE9A2980001580373656E646572406578616D706C652E696E76616C6964000155033000014F03323032362D31312D30315430323A30333A30342E3030305A0001010101010101",
        ["second"] = "03016A00455C4F5203626F7800014E033100014B034B33000156474D036D3200015D00025403E6ACA1E9A1B50001580373656E646572406578616D706C652E696E76616C6964000155033000014F03323032362D31312D30315430323A30333A30342E3030305A0001010101010101",
        ["mark"] = "03016A00455C4F5203626F7800014E033100014B034B340001010101",
        ["invalidKey"] = "03016A00455C4F5203626F7800014E03330001010101",
        ["rebuild"] = "03016A00455C4F5203626F7800014E033100014B034B35000156474D036D3100015D00025403E4B8ADE69687E4B8BBE9A2980001580373656E646572406578616D706C652E696E76616C6964000155033000014F03323032362D31312D30315430323A30333A30342E3030305A0001010101010101",
        ["moved"] = "03016A000005454A47036D3100014B033300014C036D3300010101",
        ["fetch"] = "03016A000014454D033100014E464D0331000100005203626F7800014D036D31000100144B00114A46033400014B0345584348414E47455F464958545552455F4D494D4500010101010101"
    };
    private static readonly byte[] Raw=Encoding.UTF8.GetBytes("From: sender@example.invalid\r\nTo: account@example.invalid\r\nSubject: 中文主题\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=fixture\r\n\r\n--fixture\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n中文正文\r\n--fixture\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=fixture.bin\r\nContent-Transfer-Encoding: base64\r\n\r\nAAEC/w==\r\n--fixture--\r\n");
    private sealed class Wire : HttpMessageHandler
    {
        internal Queue<(string Cmd,string Reply,string? Contains)> Next {get;}=new();
        internal int Calls;
        internal HashSet<string> Devices {get;}=[];
        internal void Expect(string cmd,string reply,string? contains=null)=>Next.Enqueue((cmd,reply,contains));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken t)
        {
            ++Calls;t.ThrowIfCancellationRequested();
            Check(request.RequestUri is {Scheme:"https",Host:"exchange.example.invalid"} && request.Headers.Authorization?.Scheme=="Basic","Wrong authority or auth");
            var (cmd,reply,contains)=Next.Dequeue();
            string? draftClient=null;
            if(cmd=="OPTIONS")Check(request.Method==HttpMethod.Options && request.RequestUri!.Query=="","OPTIONS established a device relationship");
            else
            {
                var query=System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                Check(request.Method==HttpMethod.Post && query["Cmd"]==cmd && query["User"]=="test-login","Wrong EAS command");
                Devices.Add(query["DeviceId"]!);
                Check(request.Headers.GetValues("MS-ASProtocolVersion").Single()=="16.1" && request.Content?.Headers.ContentType?.MediaType=="application/vnd.ms-sync.wbxml","Wrong wire negotiation");
                var bytes=await request.Content!.ReadAsByteArrayAsync(t);
                Check(bytes.AsSpan().StartsWith(new byte[]{3,1,106,0}),"WBXML header missing");
                if(contains!=null)Check(Encoding.UTF8.GetString(bytes).Contains(contains),"Missing expected wire content in "+cmd);
                if(reply=="draftSaved")
                {
                    int offset=bytes.AsSpan().IndexOf(new byte[]{0x4c,3});Check(offset>0,"Draft missing client id");
                    draftClient=Encoding.ASCII.GetString(bytes,offset+2,32);
                    Check(Encoding.UTF8.GetString(bytes).Contains("sample.bin") && Encoding.UTF8.GetString(bytes).Contains("blind@example.invalid"),"Draft attachment or Bcc missing");
                }
                if(cmd=="SendMail")Check(Encoding.UTF8.GetString(bytes).Contains("Bcc: blind@example.invalid"),"Lost blind envelope during MIME submission");
            }
            if(reply=="offline")throw new HttpRequestException("fixture-offline");
            if(reply=="policy")return new((HttpStatusCode)449);
            if(reply=="auth")return new(HttpStatusCode.Unauthorized);
            if(reply=="redirect")return new(HttpStatusCode.TemporaryRedirect){Headers={Location=new("https://other.example.invalid/")}};
            var result=new HttpResponseMessage(HttpStatusCode.OK);
            if(cmd=="OPTIONS"){result.Headers.Add("MS-ASProtocolVersions","14.1,16.1");result.Headers.Add("MS-ASProtocolCommands","FolderSync,Sync,ItemOperations,SendMail,MoveItems,Provision");}
            if(reply=="empty" || cmd=="OPTIONS")return result;
            var frame=Convert.FromHexString(Frames[reply]);
            if(draftClient!=null)
            {
                int offset=frame.AsSpan().IndexOf("CLIENT"u8);frame=[..frame[..offset],..Encoding.ASCII.GetBytes(draftClient),..frame[(offset+6)..]];
            }
            if(reply=="fetch")
            {
                var marker="EXCHANGE_FIXTURE_MIME"u8;
                var pos=frame.AsSpan().IndexOf(marker); frame=[..frame[..pos],..Raw,..frame[(pos+marker.Length)..]];
            }
            result.Content=new ByteArrayContent(frame);return result;
        }
    }
    private static void Check(bool value,string why)=>PluginChecks.Check(value,why);
    public static async Task Run()
    {
        var store=new SqliteMailStore(Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!,"exchange"));
        var accounts=new AccountService(store);var account=await accounts.Save(new(null,"公司样本","exchange-eas","account@example.invalid"));
        var a=new MailboxIdentity(account.Id,account.Address);
        var wire=new Wire();using var http=new HttpClient(wire);
        using var eas=new ExchangeMailbox(store,http);
        await PluginChecks.Reject(()=>eas.Configure(a.Id,"http://exchange.example.invalid","test-login","test-only",false,false,default),"HTTPS");
        await eas.Configure(a.Id,"https://exchange.example.invalid","test-login","test-only",false,false,default);
        wire.Expect("OPTIONS","empty");await eas.Check(a.Id,default);
        await PluginChecks.Reject(()=>eas.Folders(a,default),"同步关系");Check(wire.Calls==1,"Device grant ignored");
        await eas.Configure(a.Id,"https://exchange.example.invalid","test-login",null,true,true,default);
        wire.Expect("OPTIONS","auth");await PluginChecks.Reject(()=>eas.Check(a.Id,default),"身份验证失败");
        wire.Expect("OPTIONS","redirect");await PluginChecks.Reject(()=>eas.Check(a.Id,default),"重定向");
        wire.Expect("OPTIONS","empty");await eas.Check(a.Id,default);
        wire.Expect("FolderSync","policy");await PluginChecks.Reject(()=>eas.Folders(a,default),"设备或组织策略");
        wire.Expect("FolderSync","hierarchy","0");
        Check((await eas.Folders(a,default)).Any(f=>f.Name=="收件箱" && f.Role=="inbox"),"Wrong folder code page");
        wire.Expect("Sync","prime","0");wire.Expect("Sync","first","K1");wire.Expect("Sync","offline","K2");
        await PluginChecks.Reject(()=>eas.List(a,"box",null,1,default),"fixture-offline");
        using var reopened=new ExchangeMailbox(store,http);
        wire.Expect("Sync","second","K2");
        var first=await reopened.List(a,"box",null,1,default);var second=await reopened.List(a,"box",first.Next,1,default);
        Check(first.Items[0].Id!=second.Items[0].Id && second.Next==null,"Recovered pages overlap");
        wire.Expect("ItemOperations","fetch","Mailbox");
        using var copy=new MemoryStream();await reopened.Read(a,"box","m1",copy,default);
        var parsed=await new MimeReader().Parse(copy.ToArray());
        Check(copy.ToArray().SequenceEqual(Raw) && parsed.Text.Contains("中文正文") && parsed.Parts!.Any(p=>p.Name=="fixture.bin"),"Fetched MIME or attachment changed");
        var calls=wire.Calls;using var offline=new MemoryStream();await reopened.Read(a,"box","m1",offline,default);
        Check(wire.Calls==calls && offline.ToArray().SequenceEqual(Raw),"Offline read contacted server");
        var delta=await reopened.Delta(a,"box",null,100,default);
        Check(delta.Changed.Length==2 && delta.Changed.All(item => !item.OriginalUnchanged),"Missing initial delta");
        wire.Expect("Sync","mark","m1");await reopened.Mark(a,"box","m1",true,default);
        var marked=await reopened.Delta(a,"box",delta.Cursor,100,default);Check(marked.Changed.Single() is { Read: true, OriginalUnchanged: true },"Read state not reflected");
        wire.Expect("Sync","invalidKey","K4");wire.Expect("Sync","prime","0");wire.Expect("Sync","rebuild","K1");
        var recovered=await reopened.Delta(a,"box",marked.Cursor,100,default);
        Check(recovered.Removed.Contains("m2") && recovered.Changed.Any(m=>m.Id=="m1"),"Expired key retained removed messages");
        wire.Expect("MoveItems","moved","archive");Check(await reopened.Move(a,"box","m1","archive",default)=="m3","Move lost destination identity");
        using var afterMove=new MemoryStream();await reopened.Read(a,"archive","m3",afterMove,default);Check(afterMove.ToArray().SequenceEqual(Raw),"Move lost downloaded original");
        var drafts=new MailDraftStore(store);
        var draft=MailDraft.New(a.Id,a.Address) with { To=a.Address,Bcc="blind@example.invalid",Subject="草稿",Body=new("版本一") };
        var part=await drafts.AddPart(draft.Id,a.Id,"sample.bin",new MemoryStream([1,2,3]),false);
        draft=drafts.Save(draft with { Parts=[part] });
        wire.Expect("FolderSync","withDrafts");wire.Expect("Sync","draftPrime");wire.Expect("Sync","empty","D1");wire.Expect("Sync","draftSaved","版本一");
        Check(await reopened.SaveDraft(draft,default)=="draft1","Cloud draft receipt missing");
        wire.Expect("FolderSync","hierarchyNoChange");Check(await reopened.SaveDraft(draft,default)=="draft1","Duplicate draft submission");
        var service=new ProtocolMailService(store){MailboxProviders=()=>[reopened]};
        var message=new TextMail("to@example.invalid","","blind@example.invalid","本地发信","正文");
        var op=Guid.NewGuid();await service.Enqueue(op,a.Id,message);wire.Expect("SendMail","empty",op.ToString("N"));
        Check((await service.Deliver(op)).State=="sent","Exchange accepted send not recorded");
        var lost=Guid.NewGuid();await service.Enqueue(lost,a.Id,message);wire.Expect("SendMail","offline",lost.ToString("N"));
        Check((await service.Deliver(lost)).State=="unknown","Lost send receipt marked retryable");
        await PluginChecks.Reject(()=>service.Retry(lost),"安全重试");
        Check(wire.Next.Count==0 && wire.Devices.Count==1,"Lost device identity, unexpected retry or skipped request");
        Console.WriteLine("PASS Exchange ActiveSync offline wire fixtures: HTTPS/account binding, advertised commands, device consent/policy, WBXML directory, raw MIME/attachment, paging, delta restart/reset, mark/move, outbox Bcc/send uncertainty");
    }
}
