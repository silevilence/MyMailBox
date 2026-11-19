using CordiSharp;
using CordiSharp.Registry;
using MimeKit;
using MyMailBox.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailboxPlugin;

[Plugin("loopback-mailbox")]
[Inject("mailbox.host")]
public sealed class LoopbackPlugin : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var root = Environment.GetEnvironmentVariable("MYMAILBOX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMailBox");
        var host = context.Get<IPluginHost>("mailbox.host")!;
        context.Effect(() => host.AddMailboxProvider(new LoopbackMailbox(Path.Combine(root, "plugin-data", host.InstanceId, "mailboxes"))));
    }
}

internal sealed partial class LoopbackMailbox(string directory) : IMailboxProvider
{
    private sealed record Stored(string Folder, MailboxItem Summary, byte[] Raw);
    private sealed record Change(string Folder, string Id, MailboxItem? Item);
    private sealed record State(List<Stored> Messages, List<Change> Changes, Dictionary<Guid,string> Receipts);
    [JsonSerializable(typeof(State))]
    private sealed partial class StorageJson : JsonSerializerContext { }
    private static readonly MailboxFolder[] Boxes = [new("inbox","收件箱","inbox"),new("archive","归档","archive")];
    private const int Capacity = 16 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1);
    public MailboxType Type { get; } = new("loopback", "本地回环邮箱（示例）", MailboxFeatures.Read | MailboxFeatures.Send | MailboxFeatures.Delta | MailboxFeatures.Upload |
        MailboxFeatures.Move | MailboxFeatures.Mark | MailboxFeatures.Quota | MailboxFeatures.Search);

    private async Task<T> With<T>(MailboxIdentity account,bool write,Func<State,T> action,CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,account.Id.ToString("N")+".json");
            var state=File.Exists(path) ? JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path,token), StorageJson.Default.State)! : new([],[],[]);
            var result=action(state);
            if(write)
            {
                var temp=path+".new";
                await File.WriteAllBytesAsync(temp,JsonSerializer.SerializeToUtf8Bytes(state, StorageJson.Default.State),token);
                File.Move(temp,path,true);
            }
            return result;
        }
        finally { _gate.Release(); }
    }
    private static void CheckFolder(string id)
    {
        if(!Boxes.Any(f=>f.Id==id))throw new InvalidDataException("目录已不存在。");
    }
    public Task Connect(MailboxIdentity a,CancellationToken t)=>With(a,false,_=>true,t);
    public Task<MailboxFolder[]> Folders(MailboxIdentity a,CancellationToken t) { t.ThrowIfCancellationRequested();return Task.FromResult(Boxes.ToArray()); }
    public Task<MailboxPageResult> List(MailboxIdentity a,string f,string? cursor,int limit,CancellationToken t)=>Search(a,f,"",cursor,limit,t);
    public Task<MailboxPageResult> Search(MailboxIdentity a,string f,string query,string? cursor,int limit,CancellationToken t)=>With(a,false,s=>
    {
        CheckFolder(f); var offset=Index(cursor,limit);
        var rows=s.Messages.Where(m=>m.Folder==f && (m.Summary.Subject.Contains(query,StringComparison.OrdinalIgnoreCase) || m.Summary.Sender.Contains(query,StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m=>m.Summary.Date).ThenBy(m=>m.Summary.Id,StringComparer.Ordinal).Skip(offset).Take(limit+1).Select(m=>m.Summary).ToArray();
        return new MailboxPageResult(rows.Take(limit).ToArray(),rows.Length>limit?(offset+limit).ToString():null);
    },t);
    private static int Index(string? cursor,int limit)
    {
        if(limit is <1 or >200 || cursor!=null && (!int.TryParse(cursor,out var n) || n<0))throw new InvalidDataException("分页参数无效。");
        return cursor==null?0:int.Parse(cursor);
    }
    public Task<MailboxDelta> Delta(MailboxIdentity a,string f,string? cursor,int limit,CancellationToken t)=>With(a,false,s=>
    {
        CheckFolder(f); var start=Index(cursor,limit);
        if(start>s.Changes.Count)throw new InvalidDataException("游标失效，请从头同步。");
        var window=s.Changes.Skip(start).Take(limit).ToArray();
        var latest=window.Where(c=>c.Folder==f).GroupBy(c=>c.Id).Select(g=>g.Last()).ToArray();
        return new MailboxDelta(latest.Where(c=>c.Item!=null).Select(c=>c.Item!).ToArray(),latest.Where(c=>c.Item==null).Select(c=>c.Id).ToArray(),
            (start+window.Length).ToString(),start+window.Length<s.Changes.Count);
    },t);
    private static Stored Find(State s,string folder,string id)=>s.Messages.SingleOrDefault(m=>m.Folder==folder && m.Summary.Id==id) ?? throw new InvalidDataException("源邮件已改变，请刷新。");
    public async Task Read(MailboxIdentity a,string f,string id,Stream dest,CancellationToken t) =>
        await dest.WriteAsync(await With(a,false,s=>Find(s,f,id).Raw,t),t);
    public Task<MailboxQuota> Quota(MailboxIdentity a,CancellationToken t)=>With(a,false,s=>new MailboxQuota(s.Messages.Sum(m=>(long)m.Raw.Length),Capacity),t);
    private static async Task<Stored> Parse(string folder, Stream source,CancellationToken t)
    {
        CheckFolder(folder); using var bytes=new MemoryStream();
        var buffer=new byte[65536]; int count;
        while((count=await source.ReadAsync(buffer,t))!=0)
        {
            if(bytes.Length+count>Capacity)throw new InvalidDataException("示例邮箱容量为 16 MiB。");
            bytes.Write(buffer,0,count);
        }
        bytes.Position=0; using var mail=await MimeMessage.LoadAsync(bytes,t);
        return new(folder,new(Guid.NewGuid().ToString("N"),mail.Subject??"",mail.From.ToString(),mail.Date,false),bytes.ToArray());
    }
    private static string Add(State state,Stored item)
    {
        if(state.Messages.Sum(m=>(long)m.Raw.Length)+item.Raw.Length>Capacity)throw new InvalidDataException("示例邮箱容量不足。");
        state.Messages.Add(item); state.Changes.Add(new(item.Folder,item.Summary.Id,item.Summary)); return item.Summary.Id;
    }
    public async Task<string> Upload(MailboxIdentity a,string f,Stream source,CancellationToken t)
    {
        var item=await Parse(f,source,t);return await With(a,true,s=>Add(s,item),t);
    }
    public async Task<SendOutcome> Send(MailboxIdentity a,Guid op,Stream mime,string[] envelope,CancellationToken t)
    {
        if(envelope.Length!=1 || !a.Address.Equals(envelope[0],StringComparison.OrdinalIgnoreCase))
            return new("rejected","回环邮箱仅接受发往本账户地址的邮件。",[],envelope);
        var item=await Parse("inbox",mime,t);
        try
        {
            await With(a,true,s=>{if(!s.Receipts.ContainsKey(op))s.Receipts.Add(op,Add(s,item));return true;},t);
        }
        catch(InvalidDataException e){return new("rejected",e.Message,[],envelope);}
        return new("sent","邮件已送达本地回环收件箱。",envelope,[]);
    }
    public Task Mark(MailboxIdentity a,string f,string id,bool read,CancellationToken t)=>With(a,true,s=>
    {
        var old=Find(s,f,id);var item=old with{Summary=old.Summary with{Read=read}};
        s.Messages[s.Messages.IndexOf(old)]=item; s.Changes.Add(new(f,id,item.Summary));return true;
    },t);
    public Task<string> Move(MailboxIdentity a,string f,string id,string dest,CancellationToken t)=>With(a,true,s=>
    {
        CheckFolder(dest);var old=Find(s,f,id);if(dest==f)return id;
        s.Messages[s.Messages.IndexOf(old)]=old with{Folder=dest};s.Changes.Add(new(f,id,null));s.Changes.Add(new(dest,id,old.Summary));return id;
    },t);
}
