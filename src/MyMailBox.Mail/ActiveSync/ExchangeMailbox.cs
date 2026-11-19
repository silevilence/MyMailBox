using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace MyMailBox.Mail.ActiveSync;

public sealed partial class ExchangeMailbox : IMailboxProvider,IExchangeSetup,IDisposable
{
    internal const string ProviderId="exchange-eas";
    public MailboxType Type {get;}=new(ProviderId,"Exchange ActiveSync",MailboxFeatures.Read|MailboxFeatures.Delta|MailboxFeatures.Send|MailboxFeatures.Mark|MailboxFeatures.Move);
    private readonly SqliteMailStore _store;
    private readonly AccountService _accounts;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<Guid,SemaphoreSlim> _gates=new();
    public ExchangeMailbox(SqliteMailStore store,HttpClient? http=null)
    {
        _store=store;_accounts=new(store);
        _http=http??new(new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false }){Timeout=TimeSpan.FromSeconds(45)};
        _ownsHttp=http==null;
        using var db=store.Open();using var c=db.CreateCommand();
        c.CommandText="""
            CREATE TABLE IF NOT EXISTS eas_settings(account TEXT PRIMARY KEY,endpoint TEXT NOT NULL,login TEXT NOT NULL,device TEXT NOT NULL,
                allow_device INTEGER NOT NULL,allow_write INTEGER NOT NULL,version TEXT NOT NULL DEFAULT '',commands TEXT NOT NULL DEFAULT '',folder_key TEXT NOT NULL DEFAULT '0');
            CREATE TABLE IF NOT EXISTS eas_folder(account TEXT,id TEXT,parent TEXT,name TEXT,kind INTEGER,PRIMARY KEY(account,id));
            CREATE TABLE IF NOT EXISTS eas_sync(account TEXT,folder TEXT,key TEXT NOT NULL,PRIMARY KEY(account,folder));
            CREATE TABLE IF NOT EXISTS eas_item(account TEXT,folder TEXT,id TEXT,subject TEXT,sender TEXT,stamp TEXT,seen INTEGER NOT NULL,raw BLOB,PRIMARY KEY(account,folder,id));
            CREATE TABLE IF NOT EXISTS eas_draft(account TEXT,draft TEXT,version INTEGER,client TEXT NOT NULL,remote TEXT,PRIMARY KEY(account,draft,version));
            CREATE TABLE IF NOT EXISTS eas_event(seq INTEGER PRIMARY KEY,account TEXT,folder TEXT,item TEXT);
            CREATE INDEX IF NOT EXISTS eas_event_scope ON eas_event(account,folder,seq);
            """;
        c.ExecuteNonQuery(); CreateStaging();
    }
    private static SqliteCommand Command(SqliteConnection db,Guid account,string sql,params (string,object?)[] args)
    {
        var c=db.CreateCommand();c.CommandText=sql;c.Parameters.AddWithValue("$a",account.ToString());
        foreach(var (name,value) in args)c.Parameters.AddWithValue("$"+name,value??DBNull.Value);
        return c;
    }
    private async Task<T> Locked<T>(Guid account,Func<Task<T>> work,CancellationToken t)
    {
        var gate=_gates.GetOrAdd(account,_=>new(1));await gate.WaitAsync(t);
        try{return await work();}finally{gate.Release();}
    }
    private async Task<MailAccount> Account(Guid id,CancellationToken t) =>
        (await _accounts.List(t)).SingleOrDefault(a=>a.Enabled && a.Id==id && a.Provider==ProviderId) ?? throw new InvalidDataException("请选择已启用的 Exchange ActiveSync 账户。");
    public async Task<ExchangeSettings> Settings(Guid id,CancellationToken t)
    {
        var a=await Account(id,t);
        using var db=_store.Open();using var c=Command(db,id,"SELECT endpoint,login,allow_device,allow_write,version,commands FROM eas_settings WHERE account=$a");
        using var r=c.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetString(1),r.GetBoolean(2),r.GetBoolean(3),r.GetString(4),r.GetString(5).Split(',',StringSplitOptions.RemoveEmptyEntries)):
            new("",a.Address,false,false,"",[]);
    }
    public Task Configure(Guid id,string endpoint,string user,string? password,bool deviceAllowed,bool writeAllowed,CancellationToken t)=>Locked(id,async()=>
    {
        await Account(id,t);
        if(!Uri.TryCreate(endpoint.Trim(),UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length!=0 || uri.Query.Length!=0 || uri.Fragment.Length!=0)
            throw new InvalidDataException("请输入由管理员提供的 HTTPS 服务器或完整 ActiveSync 地址。");
        if(uri.AbsolutePath=="/")uri=new Uri(uri,"Microsoft-Server-ActiveSync");
        user=user.Trim();
        if(user.Length==0 || user.Contains(':') || user.Any(char.IsControl))throw new InvalidDataException("请输入有效的 Exchange 登录身份。");
        using var db=_store.Open();using var tx=db.BeginTransaction();
        using var c=Command(db,id,"SELECT endpoint,login FROM eas_settings WHERE account=$a");c.Transaction=tx;
        using(var r=c.ExecuteReader())if(r.Read() && (r.GetString(0)!=uri.AbsoluteUri || r.GetString(1)!=user))
            throw new InvalidDataException("服务器或登录身份变更时请新建账户，以免混用已同步的邮件。");
        c.CommandText="SELECT credential_ref FROM accounts WHERE id=$a AND removed=0 AND enabled=1";
        var reference=c.ExecuteScalar() as string;
        if(!string.IsNullOrEmpty(password))
        {
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("凭据保护需要 Windows。");
            var plain=Encoding.UTF8.GetBytes(password);byte[] cipher;
            try { cipher=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser); } finally { CryptographicOperations.ZeroMemory(plain); }
            reference??=Guid.NewGuid().ToString();
            c.CommandText="INSERT INTO credentials(id,protected) VALUES($ref,$cipher) ON CONFLICT(id) DO UPDATE SET protected=excluded.protected";
            c.Parameters.AddWithValue("$ref",reference);c.Parameters.AddWithValue("$cipher",cipher);c.ExecuteNonQuery();
        }
        if(reference==null)throw new InvalidDataException("请先输入此账户的登录凭据。");
        c.CommandText="""
            INSERT INTO eas_settings(account,endpoint,login,device,allow_device,allow_write) VALUES($a,$endpoint,$user,$device,$ok,$write)
            ON CONFLICT(account) DO UPDATE SET allow_device=$ok,allow_write=$write;
            UPDATE accounts SET credential_ref=$cred,capabilities=(capabilities & ~2),auth_state='等待连接验证' WHERE id=$a
            """;
        c.Parameters.AddWithValue("$cred",reference);c.Parameters.AddWithValue("$endpoint",uri.AbsoluteUri);c.Parameters.AddWithValue("$user",user);
        c.Parameters.AddWithValue("$device",Guid.NewGuid().ToString("N").ToUpperInvariant());c.Parameters.AddWithValue("$ok",deviceAllowed);
        c.Parameters.AddWithValue("$write",writeAllowed && deviceAllowed);c.ExecuteNonQuery();t.ThrowIfCancellationRequested();tx.Commit();return true;
    },t);
    public async Task<ExchangeSettings> Check(Guid id,CancellationToken t)
    {
        return await Locked(id,async()=>
        {
            var s=await Settings(id,t);
            using var request=await Request(id,s,HttpMethod.Options,s.Endpoint,t);
            using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,t);
            await CheckHttp(id,response,t);
            string[] Header(string name)=>response.Headers.TryGetValues(name,out var values)?values.SelectMany(v=>v.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).ToArray():[];
            var versions=Header("MS-ASProtocolVersions");var commands=Header("MS-ASProtocolCommands");
            var version=new[]{"16.1","16.0","14.1"}.FirstOrDefault(versions.Contains);
            if(version==null || !new[]{"FolderSync","Sync","ItemOperations"}.All(commands.Contains))throw new InvalidDataException("此服务器缺少可用的 ActiveSync 版本或目录、同步、原文命令。");
            using var db=_store.Open();using var c=Command(db,id,"""
                UPDATE eas_settings SET version=$v,commands=$cmd WHERE account=$a;
                UPDATE accounts SET auth_state='能力查询已通过',capabilities=((capabilities & ~2) | $send) WHERE id=$a
                """,("v",version),("cmd",string.Join(',',commands)),("send",s.DeviceAllowed && s.WriteAllowed && commands.Contains("SendMail")?2:0));
            using var tx=db.BeginTransaction();c.Transaction=tx;c.ExecuteNonQuery();tx.Commit();
            return s with{ Version=version,Commands=commands };
        },t);
    }
    private async Task<HttpRequestMessage> Request(Guid id,ExchangeSettings s,HttpMethod method,string url,CancellationToken t)
    {
        if(s.Endpoint.Length==0)throw new InvalidDataException("请先保存此账户的 Exchange 服务器和登录身份。");
        var secret=await _accounts.ReadCredential(id,t) ?? throw new InvalidDataException("请为此账户重新输入 Exchange 凭据。");
        var request=new HttpRequestMessage(method,url);
        request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(s.User+":"+secret)));
        request.Headers.UserAgent.ParseAdd("MyMailBox/1.0");return request;
    }
    private async Task CheckHttp(Guid id,HttpResponseMessage response,CancellationToken t)
    {
        if(response.IsSuccessStatusCode)return;
        if(response.StatusCode==HttpStatusCode.Unauthorized){await _accounts.AuthenticationFailed(id,t);throw MailboxFailure.Mark(new Refused("Exchange 身份验证失败，请更新凭据。"), MailboxFailureKind.Authorization);}
        if((int)response.StatusCode is 403 or 449)throw MailboxFailure.Mark(new Refused("Exchange 设备或组织策略阻止了操作，请联系管理员核对设备准入。"), MailboxFailureKind.Policy);
        if((int)response.StatusCode is >=300 and <400 or 451)throw new InvalidDataException("服务器要求重定向，请管理员确认新地址后新建连接。");
        if((int)response.StatusCode is 429 or 503)throw MailboxFailure.Mark(new InvalidDataException("Exchange 暂时限流或不可用，请稍后重试。"), MailboxFailureKind.Throttled,
            response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5)));
        throw new InvalidDataException($"Exchange 返回 HTTP {(int)response.StatusCode}。");
    }
    private async Task<EasNode?> Post(Guid id,string command,EasNode body,CancellationToken t,bool write=false)
    {
        var s=await Settings(id,t);
        if(!s.DeviceAllowed || s.Version.Length==0)throw new InvalidDataException("请先确认允许本机建立同步关系，并检查服务器能力。");
        if(write && !s.WriteAllowed)throw new InvalidDataException("请先在此账户连接设置中允许发信与邮件整理。");
        if(!s.Commands.Contains(command))throw new NotSupportedException("服务器未声明 "+command+"。");
        using var db=_store.Open();using var c=Command(db,id,"SELECT device FROM eas_settings WHERE account=$a");
        var url=s.Endpoint+"?Cmd="+command+"&User="+Uri.EscapeDataString(s.User)+"&DeviceId="+(string)c.ExecuteScalar()!+"&DeviceType=MyMailBox";
        using var request=await Request(id,s,HttpMethod.Post,url,t);
        request.Headers.Add("MS-ASProtocolVersion",s.Version);request.Headers.Add("X-MS-PolicyKey","0");
        request.Content=new ByteArrayContent(Wbxml.Write(body));request.Content.Headers.ContentType=new("application/vnd.ms-sync.wbxml");
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,t);
        await CheckHttp(id,response,t);
        await using var stream=await response.Content.ReadAsStreamAsync(t);
        using var data=new MemoryStream();using var limit=new AttachmentService.CountingStream(data,MimeReader.MaxMessageBytes+1024*1024,null,"Exchange 响应超过本机阅读限制。");
        await stream.CopyToAsync(limit,t);if(data.Length==0)return null;
        var result=Wbxml.Read(data.ToArray());
        if(result.Tag!=body.Tag)throw new InvalidDataException("Exchange 响应命令不匹配。");
        return result;
    }
    private static void Status(EasNode n,Tag tag,string expected="1")
    {
        var code=n.Value(tag);if(code==expected)return;
        if(code is "140" or "141" or "142" or "143" or "144")throw MailboxFailure.Mark(new InvalidDataException("Exchange 要求设备策略，请联系管理员核对准入。"), MailboxFailureKind.Policy);
        throw new InvalidDataException("Exchange 操作未完成，协议状态 "+(int.TryParse(code,out var value)?value.ToString():"无效")+"。");
    }
    public Task Connect(MailboxIdentity a,CancellationToken t)=>Check(a.Id,t);
    public void Dispose(){if(_ownsHttp)_http.Dispose();}
}
