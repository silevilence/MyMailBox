using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static partial class PerformanceFixture
{
    private static SqliteMailStore Open(int size,string state)
    {
        var directory=Path.Combine(Root(size),state,"mail");
        if(!File.Exists(Path.Combine(directory,"mail.db")))throw new InvalidOperationException("请先准备样本。");
        return new(directory);
    }
    public static async Task Index(int size)
    {
        var store=Open(size,"indexed");var service=new MailSearchService(store,new ProtocolMailService(store));
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT count(*) FROM body_index_state";
        if(Convert.ToInt32(c.ExecuteScalar())<size)
        {
            // A normal search resumes incomplete indexing after interruption.
            var result=await service.Search(new([new("subject",SearchMatch.Equals,"基准索引校验")]));
            if(result.IndexedBodies<size)throw new InvalidDataException("索引未完成。");
        }
        Console.WriteLine($"PASS {size} indexed fixture ready");
    }
    [StructLayout(LayoutKind.Sequential)]private struct Io {public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessIoCounters(IntPtr process,out Io counters);
    private sealed record Timing(string Cache,string Operation,double Milliseconds);
    public static async Task Measure(int size,string state,bool sync)
    {
        var store=Open(size,state);var protocol=new ProtocolMailService(store);var search=new MailSearchService(store,protocol);
        var read=new MyMailBox.Mail.MimeReader();var folders=await store.Folders();
        var folder=folders.First(f=>f.AccountId==SqliteMailStore.LocalAccount&&f.Name=="第 1 层");
        var timings=new List<Timing>();using var stop=new CancellationTokenSource();int syncCount=0;Task background=Task.CompletedTask;
        if(sync)
        {
            var accounts=new AccountService(store);
            var a=(await accounts.List()).FirstOrDefault(a=>a.Name=="测量用同步")??await accounts.Save(new(null,"测量用同步","imap","alice@localhost"));
            var pin=File.ReadAllText(Path.Combine(PluginChecks.Repository,"artifacts/protocol-acceptance/certificate-sha256.txt")).Trim();
            await protocol.Save(a.Id,new(new("127.0.0.1",3993,"alice@localhost",CertificateSha256:pin)),"local-alice",null);
            // Reconcile real INBOX summaries concurrently without changing flags or reading remote bodies.
            await protocol.Sync(a.Id,"INBOX",5);syncCount++;
            background=Task.Run(async()=>{try{while(true){await protocol.Sync(a.Id,"INBOX",5,null,stop.Token);Interlocked.Increment(ref syncCount);await Task.Delay(150,stop.Token);}}
                catch(OperationCanceledException)when(stop.IsCancellationRequested){}});
        }
        using var process=Process.GetCurrentProcess();GetProcessIoCounters(process.Handle,out var ioStart);
        var cpuStart=process.TotalProcessorTime;
        int indexStart=IndexCount();
        int IndexCount(){using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT count(*) FROM body_index_state";return Convert.ToInt32(c.ExecuteScalar());}
        async Task<T> Time<T>(string label,string op,Func<Task<T>> action)
        {
            var w=Stopwatch.StartNew();var result=await action();timings.Add(new(label,op,w.Elapsed.TotalMilliseconds));return result;
        }
        try
        {
            for(int i=0;i<11;i++)
            {
                var label=i==0?"first-request":"warm";
                var list=await Time(label,"list",()=>store.List(folder.Id,0,25));
                await Time(label,"page",()=>store.List(folder.Id,(i+1)*10,25));
                var normal=list.Items.First(m=>m.Size<15000);
                await Time(label,"read-mime",async()=>await read.Parse(await store.Read(normal.Id)));
                if(state=="indexed")
                {
                    var result=await Time(label,"subject-search",()=>search.Search(new([new("subject",SearchMatch.Contains,"季度会议")],Limit:25)));
                    if(result.Items.Count!=25)throw new InvalidDataException("搜索首批不完整。");
                    result=await Time(label,"body-search",()=>search.Search(new([]){Condition=ConditionTree.Parse("body:采购")}));
                    if(result.Items.Count!=25)throw new InvalidDataException("正文首批不完整。");
                }
                else if(i==0)
                {
                    using var cancel=new CancellationTokenSource(150);
                    try{await search.Search(new([new("body",SearchMatch.Contains,"资料")]),cancel.Token);}
                    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
                }
            }
        }
        finally {stop.Cancel();await background;}
        GetProcessIoCounters(process.Handle,out var ioEnd);process.Refresh();
        var stats=timings.GroupBy(t=>(t.Cache,t.Operation)).Select(g=>new{cache=g.Key.Cache,operation=g.Key.Operation,n=g.Count(),
            p95Ms=Math.Round(g.OrderBy(t=>t.Milliseconds).ElementAt((int)Math.Ceiling(g.Count()*.95)-1).Milliseconds,2)}).ToArray();
        var record=new{count=size,state,sync,syncCount,indexStart,indexEnd=IndexCount(),date=DateTimeOffset.Now,stats,
            workingSet=process.WorkingSet64,peakWorkingSet=process.PeakWorkingSet64,cpuMilliseconds=(process.TotalProcessorTime-cpuStart).TotalMilliseconds,
            readBytes=ioEnd.ReadBytes-ioStart.ReadBytes,writeBytes=ioEnd.WriteBytes-ioStart.WriteBytes,timings};
        var dest=Path.Combine(Root(size),$"measure-{state}-{(sync?"sync":"idle")}.json");File.WriteAllText(dest,JsonSerializer.Serialize(record,Json));
        Console.WriteLine("PASS "+dest+"\n"+string.Join("\n",stats.Select(x=>$"{x.cache} {x.operation} p95 {x.p95Ms} ms")));
    }
}
