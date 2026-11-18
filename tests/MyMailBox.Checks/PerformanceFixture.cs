using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MimeKit;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class PerformanceFixture
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    internal static string Root(int size)=>Path.Combine(PluginChecks.Repository,"artifacts","performance-v1",size.ToString(CultureInfo.InvariantCulture));
    private const int LargeSize=2*1024*1024,SmallSize=4096;
    private static byte[] Pattern(int size){var data=new byte[size];new Random(731).NextBytes(data);return data;}
    private static readonly byte[] Large=Pattern(LargeSize),Small=Pattern(SmallSize);
    private static readonly string[] Themes=["部门计划","发票核对","客户反馈","测试简报","季度会议","项目交付","合同审批","资料存档"];
    private static readonly string[] Paragraphs=["请核对本周进度与负责人，并在会议前补充最新的测试结果。","采购明细、单位和预计到货日期已更新，附件保留原始报价。","项目组汇总了各部门反馈，同时列出需要跟进的三个问题。","We compared the available options and recorded the reasons for the next review."];
    private static DateTimeOffset Date(int i)=>new DateTimeOffset(2025,1,1,8,0,0,TimeSpan.Zero).AddMinutes(i*3);
    private static Guid Id(int i)=>new(i,0,0,new byte[]{0,0,0,0,0,0,0,82});
    private static (byte[] Raw,string Subject,int BodySize,int AttachmentSize,bool Html) Sample(int i)
    {
        var subject=Themes[i%Themes.Length]+" "+i.ToString("D6");
        var body=string.Join("\n",Enumerable.Range(0,3+i%13).Select(j=>Paragraphs[(i+j)%Paragraphs.Length]))+"\n记录 "+i;
        bool html=i%3==0;int size=i%2048==0?LargeSize:i%11==0?SmallSize:0;
        using var mime=new MimeMessage{Subject=subject,MessageId=$"bench-{i}@example.test",Date=Date(i)};
        mime.From.Add(new MailboxAddress("样本发件人","sender@example.test"));mime.To.Add(new MailboxAddress("收件人","reader@example.test"));
        var builder=new BodyBuilder{TextBody=body};if(html)builder.HtmlBody="<html><body><h2>"+subject+"</h2><p>"+body.Replace("\n","</p><p>")+"</p></body></html>";
        if(size>0)builder.Attachments.Add("材料-"+i+".bin",size==LargeSize?Large:Small);
        mime.Body=builder.ToMessageBody();
        void Boundaries(MimeEntity entity,int depth){if(entity is Multipart multi){multi.Boundary="bench_"+i+"_"+depth;foreach(var part in multi)Boundaries(part,depth+1);}}
        Boundaries(mime.Body,0);using var stream=new MemoryStream();var format=FormatOptions.Default.Clone();format.NewLineFormat=NewLineFormat.Dos;mime.WriteTo(format,stream);
        return(stream.ToArray(),subject,Encoding.UTF8.GetByteCount(body),size,html);
    }
    public static async Task Prepare(int size)
    {
        var root=Root(size);var store=new SqliteMailStore(Path.Combine(root,"source","mail"));var accounts=new AccountService(store);
        var local=new LocalMailboxService(store,accounts,new MyMailBox.Mail.MimeReader());var protocol=new ProtocolMailService(store);
        _=new MailSearchService(store,protocol);
        var folders=new List<Guid>();
        for(int i=0;i<10;i++)
        {
            string name="基准邮箱 "+i;var a=i==0?SqliteMailStore.LocalAccount:((await accounts.List()).FirstOrDefault(a=>a.Name==name)??await local.Create(new(null,name,"local"))).Id;
            Guid? parent=null;
            for(int depth=0;depth<4;depth++)
            {
                var label="第 "+(depth+1)+" 层";parent=(await store.Folders()).FirstOrDefault(f=>f.AccountId==a&&f.ParentId==parent&&f.Name==label)?.Id??(await local.CreateFolder(a,parent,label)).Id;
                folders.Add(parent.Value);
            }
        }
        using var db=store.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="CREATE TABLE IF NOT EXISTS fixture_sample(n INTEGER PRIMARY KEY,body INTEGER NOT NULL,attachment INTEGER NOT NULL,html INTEGER NOT NULL)";
        cmd.ExecuteNonQuery();cmd.CommandText="SELECT count(*) FROM fixture_sample";int start=Convert.ToInt32(cmd.ExecuteScalar());
        for(int offset=start;offset<size;offset+=250)
        {
            using var tx=db.BeginTransaction();cmd.Transaction=tx;
            for(int i=offset;i<Math.Min(size,offset+250);i++)
            {
                var s=Sample(i);cmd.Parameters.Clear();cmd.CommandText="""
                    INSERT INTO messages(id,folder_id,subject,sender,recipient,date,size,is_read,raw,sha256)
                    VALUES($id,$folder,$subject,'sender@example.test','reader@example.test',$date,$size,$read,$raw,$hash);
                    INSERT INTO fixture_sample VALUES($n,$body,$attachment,$html);
                    """;
                cmd.Parameters.AddWithValue("$id",Id(i).ToString());cmd.Parameters.AddWithValue("$folder",folders[(i*17)%folders.Count].ToString());
                cmd.Parameters.AddWithValue("$subject",s.Subject);cmd.Parameters.AddWithValue("$date",Date(i).ToString("O"));cmd.Parameters.AddWithValue("$size",s.Raw.Length);
                cmd.Parameters.AddWithValue("$read",i%4!=0);cmd.Parameters.AddWithValue("$raw",s.Raw);cmd.Parameters.AddWithValue("$hash",Convert.ToHexString(SHA256.HashData(s.Raw)));
                cmd.Parameters.AddWithValue("$n",i);cmd.Parameters.AddWithValue("$body",s.BodySize);cmd.Parameters.AddWithValue("$attachment",s.AttachmentSize);cmd.Parameters.AddWithValue("$html",s.Html);cmd.ExecuteNonQuery();
            }
            tx.Commit();cmd.Transaction=null;
        }
        cmd.CommandText="""
            SELECT count(*),avg(body),sum(html),count(*) FILTER(WHERE attachment>0),avg(nullif(attachment,0)),max(attachment),
                (SELECT count(*) FROM messages),(SELECT avg(size) FROM messages),(SELECT count(*) FROM folders f JOIN accounts a ON a.id=f.account_id WHERE a.provider='local'),(SELECT count(*) FROM accounts WHERE provider='local')
            FROM fixture_sample
            """;
        using(var r=cmd.ExecuteReader())
        {
            r.Read();if(r.GetInt32(0)!=size||r.GetInt32(6)!=size)throw new InvalidDataException("样本数量不符。");
            var manifest=new{version=1,count=size,accounts=r.GetInt32(9),folders=r.GetInt32(8),populatedFolders=folders.Count,depth=4,meanBodyBytes=r.GetDouble(1),
                html=r.GetInt32(2),attachments=r.GetInt32(3),meanAttachmentBytes=r.GetDouble(4),largestAttachment=r.GetInt32(5),meanMimeBytes=r.GetDouble(7),
                distribution="主题八类；每三封一封 HTML；每 11 封一个 4 KiB 附件，每 2048 封优先使用 2 MiB 附件；每四封一封未读。"};
            File.WriteAllText(Path.Combine(root,"manifest.json"),JsonSerializer.Serialize(manifest,Json));
        }
        cmd.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";cmd.ExecuteNonQuery();
        var indexed=Path.Combine(root,"indexed","mail");Directory.CreateDirectory(indexed);
        var target=Path.Combine(indexed,"mail.db");if(!File.Exists(target))
        {
            string staging=target+".building-"+Guid.NewGuid().ToString("N");
            using(var copy=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=staging,Pooling=false}.ToString())){copy.Open();db.BackupDatabase(copy);}
            File.Move(staging,target);
        }
        Console.WriteLine($"PASS {size} MIME fixture, reused {start} records: {root}");
    }
    public static async Task PrepareUi(int size)
    {
        var template=Open(size,"indexed");
        var dir=Path.Combine(Root(size),"ui","mail");Directory.CreateDirectory(dir);
        if(!File.Exists(Path.Combine(dir,"mail.db")))
        {
            using var from=template.Open();
            var temporary=Path.Combine(dir,"prepare-"+Guid.NewGuid()+".db");
            using(var to=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=temporary,Pooling=false}.ToString())){to.Open();from.BackupDatabase(to);}
            File.Move(temporary,Path.Combine(dir,"mail.db"));
        }
        var store=new SqliteMailStore(dir);var protocols=new ProtocolMailService(store);var auto=new MailAutomationService(store,new(store,protocols),protocols);
        var folders=await store.Folders();var owner=SqliteMailStore.LocalAccount;
        async Task<Guid> Folder(string name)=>folders.FirstOrDefault(f=>f.AccountId==owner&&f.Name==name)?.Id??await protocols.Create(owner,null,name);
        var input=await Folder("后台归档来源");var output=await Folder("后台归档结果");
        // Each measurement needs a full frozen batch, even if the previous window closed
        // with just one archive item left. Keep the large index and its cached messages.
        foreach (var run in await auto.Runs(Id(-83)))
            if (run.Cause.StartsWith("manual/performance/",StringComparison.Ordinal) && run.State is "ready" or "running")
                await auto.Cancel(run.Id);
        var page=await store.List(input,0,20);
        for(int i=page.Items.Count;i<12;i++){var sample=Sample(i+1);await store.Import(input,sample.Raw,await new MyMailBox.Mail.MimeReader().Parse(sample.Raw));}
        var plan=new AutomationPlan(Id(-83),0,"测量期间归档",new(owner,input,"","count>=1",0,0),true)
        {Target=new(owner,input,false,"",null,null,0,0,true,"保留",SearchOrder.Oldest,12,0,null),Archive=new(owner,output,false,540,"UTC")};
        if(!(await auto.Plans()).Any(p=>p.Id==plan.Id))await auto.Save(plan);
        Console.WriteLine($"PASS reusable {size} indexed UI workspace with separate background archive scope");
    }
}
