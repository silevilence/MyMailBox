using MyMailBox.Contracts;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MyMailBox.Mail;

public sealed partial class MailAutomationService : IMailAutomation
{
    private readonly SqliteMailStore _store;private readonly MailSearchService _search;private readonly ProtocolMailService _protocols;private readonly TimeProvider _clock;
    public MailAutomationService(SqliteMailStore store,MailSearchService search,ProtocolMailService protocols,TimeProvider? clock=null,Action<string>? notify=null,bool activityNotices=false)
    {
        _store=store;_search=search;_protocols=protocols;_clock=clock??TimeProvider.System;_notify=notify;_activityNotices=activityNotices;
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="""
            CREATE TABLE IF NOT EXISTS automation_plan(id TEXT PRIMARY KEY,version INTEGER NOT NULL,definition TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS automation_gate(id TEXT PRIMARY KEY,version INTEGER NOT NULL,latched INTEGER NOT NULL,last_fire INTEGER);
            CREATE TABLE IF NOT EXISTS automation_run(id TEXT PRIMARY KEY,plan TEXT NOT NULL,cause TEXT NOT NULL,state TEXT NOT NULL,detail TEXT NOT NULL,at INTEGER NOT NULL,
                definition TEXT NOT NULL,snapshot TEXT NOT NULL,UNIQUE(plan,cause));
            CREATE TABLE IF NOT EXISTS automation_item(run TEXT NOT NULL,ordinal INTEGER NOT NULL,op TEXT NOT NULL,destination TEXT,state TEXT NOT NULL,detail TEXT NOT NULL,PRIMARY KEY(run,ordinal));
            CREATE TABLE IF NOT EXISTS automation_step(run TEXT NOT NULL,item INTEGER NOT NULL,step INTEGER NOT NULL,op TEXT NOT NULL,
                kind INTEGER NOT NULL,state TEXT NOT NULL,attempts INTEGER NOT NULL,detail TEXT NOT NULL,PRIMARY KEY(run,item,step));
            CREATE TABLE IF NOT EXISTS automation_gate_sample(run TEXT PRIMARY KEY,report TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS automation_retired(cause_hash TEXT PRIMARY KEY);
            CREATE INDEX IF NOT EXISTS automation_run_by_time ON automation_run(at);
            CREATE TABLE IF NOT EXISTS automation_outcome(id TEXT PRIMARY KEY,success INTEGER NOT NULL,at INTEGER NOT NULL);
            """;c.ExecuteNonQuery();
    }
    public Task<IReadOnlyList<MailFolder>> ArchiveFolders(CancellationToken token=default)=>_protocols.Destinations(Guid.Empty,token);
    public Task<SearchLibrary> Library(CancellationToken token=default)=>_search.Library(token);
    public Task<IReadOnlyList<AutomationPlan>> Plans(CancellationToken token=default)=>Task.Run<IReadOnlyList<AutomationPlan>>(()=>
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="SELECT definition FROM automation_plan ORDER BY rowid";
        using var r=c.ExecuteReader();var list=new List<AutomationPlan>();while(r.Read())list.Add(JsonSerializer.Deserialize<AutomationPlan>(r.GetString(0))!);return list;
    },token);
    public async Task<AutomationPlan> Save(AutomationPlan plan,CancellationToken token=default)
    {
        if(string.IsNullOrWhiteSpace(plan.Name)||plan.When.Hysteresis<0||plan.When.CooldownMinutes<0)throw new InvalidDataException("请填写任务名、非负迟滞和冷却时间。");
        await Inspect(plan,false,token);
        if(plan.Target is not null)await Select(plan,null,token);
        await ValidateActions(plan,token);
        if(plan.Archive is {} archive)
        {
            if(plan.Target is null||archive.LocalMinute is <0 or >=1440)throw new InvalidDataException("归档任务需要目标集合和有效的每日时刻。");
            _=TimeZoneInfo.FindSystemTimeZoneById(archive.Zone);
            var accounts=await new AccountService(_store).List(token);
            if(!accounts.Any(a=>a.Id==archive.Account && a.Enabled && a.Provider=="local") || !(await Library(token)).Scopes.Any(s=>s.Account==archive.Account&&s.Folder==archive.Folder))
                throw new InvalidDataException("请选择可写的本地归档目录。");
        }
        using var db=_store.Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
        c.CommandText="SELECT version FROM automation_plan WHERE id=$id";c.Parameters.AddWithValue("$id",plan.Id.ToString());
        if(Convert.ToInt32(c.ExecuteScalar())!=plan.Version)throw new InvalidDataException("任务已修改，请重新载入。");
        plan=plan with{Version=plan.Version+1};c.CommandText="INSERT OR REPLACE INTO automation_plan VALUES($id,$v,$json)";
        c.Parameters.AddWithValue("$v",plan.Version);c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(plan));c.ExecuteNonQuery();SaveTriggers(plan,tx);tx.Commit();return plan;
    }
    public async Task<ConditionReport> Inspect(AutomationPlan plan,bool observe=false,CancellationToken token=default)
    {
        var library=await Library(token);var w=plan.When;
        if(!library.Scopes.Any(s=>s.Account==w.Account&&s.Folder==w.Folder))throw new InvalidDataException("前置条件统计范围已失效。");
        return await Task.Run(()=>Measure(plan,observe,token),token);
    }
    private ConditionReport Measure(AutomationPlan plan,bool observe,CancellationToken token)
    {
        var now=_clock.GetUtcNow();var w=plan.When;var tree=ConditionTree.Parse(w.Expression);
        var mail=_search.CountScope(w,now,token);bool known=mail.Complete&&mail.Unknown==0;
        using var db=_store.Open();using var c=db.CreateCommand();c.Parameters.AddWithValue("$id",plan.Id.ToString());
        c.CommandText="SELECT success FROM automation_outcome WHERE id=$id";var previous=c.ExecuteScalar();
        var values=new List<MetricValue>();
        void Add(string id,string name,decimal? value,string? basis=null)=>values.Add(new(id,name,value,basis??mail.Basis,basis is null?mail.At:now));
        Add("count","匹配邮件数",known?mail.Count:null);Add("unread","匹配的未读数",known?mail.Unread:null);
        Add("logical","匹配邮件逻辑字节",known?mail.Logical:null);Add("cached","匹配且已缓存的 MIME 字节",mail.Unknown==0?mail.Cached:null,"当前本地已有原文");
        Add("exists","存在匹配邮件",mail.Pending.HasValue && mail.Count>0?1:known?0:null);
        Add("all","范围内全部匹配",mail.Pending.HasValue&&mail.Outside>0?0:known?(mail.Count>0?1:0):null);
        Add("pending","待同步摘要数",mail.Pending);Add("quota","服务器配额使用百分比",mail.Quota);
        Add("disk","数据库实际占用字节",Allocated(),"工作区数据库、WAL 与共享内存文件的实际占用");
        Add("free","可用磁盘字节",new DriveInfo(Path.GetPathRoot(_store.DatabasePath)!).AvailableFreeSpace,"工作区所在卷");
        Add("net","系统网络可用",NetworkInterface.GetIsNetworkAvailable()?1:0,"本机网络接口状态");
        Add("ac","接入交流电",GetSystemPowerStatus(out var power)&&power.Ac<=1?power.Ac:null,"Windows 电源状态");
        var local=TimeZoneInfo.ConvertTime(now,TimeZoneInfo.Local);Add("minute","本地当日分钟",local.Hour*60+local.Minute,TimeZoneInfo.Local.Id);
        Add("last","上次任务成功",previous is long v?v:null,"任务最近一次执行结果");
        using var tx=db.BeginTransaction(deferred:!observe);c.Transaction=tx;
        c.Parameters.AddWithValue("$v",plan.Version);
        if(observe)
        {
            c.CommandText="SELECT definition FROM automation_plan WHERE id=$id AND version=$v";
            if(c.ExecuteScalar() is not string saved || JsonSerializer.Serialize(JsonSerializer.Deserialize<AutomationPlan>(saved))!=JsonSerializer.Serialize(plan))throw new InvalidDataException("请先保存当前任务版本再记录采样。");
        }
        c.CommandText="SELECT latched,last_fire FROM automation_gate WHERE id=$id AND version=$v";
        bool? prior=null;long? last=null;using(var r=c.ExecuteReader())if(r.Read()){prior=r.GetBoolean(0);last=r.IsDBNull(1)?null:r.GetInt64(1);}
        bool? Test(ConditionNode n,bool inverted=false)
        {
            if(n.Criterion is {} p)
            {
                var m=values.SingleOrDefault(m=>m.Id==p.Field)??throw new ConditionException(n.Position,"未知统计字段 "+p.Field);
                decimal threshold;
                if(p.Value=="true")threshold=1;else if(p.Value=="false")threshold=0;
                else if(!decimal.TryParse(p.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out threshold))throw new ConditionException(n.Position,"阈值需要数值。");
                decimal slack=prior==true?(inverted?-w.Hysteresis:w.Hysteresis):0;
                if(p.Match is not (SearchMatch.AtLeast or SearchMatch.AtMost or SearchMatch.Before or SearchMatch.Equals))throw new ConditionException(n.Position,"统计条件支持 >=、<=、< 或 =。");
                if(m.Value is not decimal a)return null;
                return p.Match switch{SearchMatch.AtLeast=>a>=threshold-slack,SearchMatch.AtMost=>a<=threshold+slack,SearchMatch.Before=>a<threshold+slack,_=>a==threshold};
            }
            if(n.Logic==ConditionLogic.Not)return !Test(n.Children.Single(),!inverted);
            bool and=n.Logic==ConditionLogic.And;bool? result=and;
            foreach(var child in n.Children){var test=Test(child,inverted);result=and?result&test:result|test;}return result;
        }
        var state=Test(tree.Root);bool crossed=plan.Enabled&&state==true&&prior==false&&(last is null||now.ToUnixTimeSeconds()-last>=60L*w.CooldownMinutes);
        if(observe && state.HasValue)
        {
            c.CommandText="INSERT OR REPLACE INTO automation_gate VALUES($id,$v,$on,$last)";
            c.Parameters.AddWithValue("$on",state.Value);c.Parameters.AddWithValue("$last",(object?)(crossed?now.ToUnixTimeSeconds():last)??DBNull.Value);c.ExecuteNonQuery();
        }
        tx.Commit();string detail=state is null?"统计未知或覆盖不完整。":prior is null?"首次有效采样建立基线。":crossed?"已跨越阈值且冷却结束。":"未产生新的跨越。";
        return new(state is null?ConditionResult.Unknown:state.Value?ConditionResult.True:ConditionResult.False,values,crossed,detail,now);
    }
    private long? Allocated()
    {
        long bytes=0;foreach(var path in new[]{_store.DatabasePath,_store.DatabasePath+"-wal",_store.DatabasePath+"-shm"})
        {if(!File.Exists(path))continue;uint low=GetCompressedFileSize(path,out uint high);if(low==uint.MaxValue&&Marshal.GetLastPInvokeError()!=0)return null;bytes+=((long)high<<32)|low;}return bytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Power { public byte Ac,Battery,Percent,Flag;public uint Lifetime,FullLifetime; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetSystemPowerStatus(out Power value);
    [DllImport("kernel32.dll",EntryPoint="GetCompressedFileSizeW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetCompressedFileSize(string path,out uint high);
}
