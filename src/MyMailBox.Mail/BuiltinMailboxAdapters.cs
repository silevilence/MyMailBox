using MyMailBox.Contracts;
using MailKit;
using MailKit.Net.Imap;

namespace MyMailBox.Mail;

internal static class MailboxPaging
{
    internal static int Offset(string? cursor) => cursor == null ? 0 : int.TryParse(cursor, out var n) && n >= 0 ? n : throw new InvalidDataException("邮箱分页游标无效。");
}

public static class BuiltinMailboxAdapters
{
    public static async Task<byte[]> ReadOriginal(IMailboxProvider provider, MailboxIdentity account, string folder, string item, CancellationToken token)
    {
        using var data = new MemoryStream();
        using var destination = new AttachmentService.CountingStream(data, MimeReader.MaxMessageBytes, null, "邮件超过 25 MiB 阅读限制。");
        await provider.Read(account, folder, item, destination, token);
        return data.ToArray();
    }

    public static IMailboxProvider[] Create(SqliteMailStore store, GraphMailService graph, ProtocolMailService protocols) =>
        [new Local(store, protocols), new Graph(store, graph), new Imap("imap", protocols), new Imap("imap-smtp", protocols)];

    private sealed class Local(SqliteMailStore store, ProtocolMailService protocols) : IMailboxProvider
    {
        public MailboxType Type { get; } = new("local", "本地邮箱", MailboxFeatures.Read | MailboxFeatures.Mark | MailboxFeatures.Move | MailboxFeatures.Upload);
        public Task Connect(MailboxIdentity a, CancellationToken t) => Folders(a,t);
        public async Task<MailboxFolder[]> Folders(MailboxIdentity a,CancellationToken t) =>
            (await ((IMailOrganizer)protocols).Folders(a.Id,t)).Select(f=>new MailboxFolder(f.Id.ToString(),f.Path,f.Role)).ToArray();
        public async Task<MailboxPageResult> List(MailboxIdentity a,string f,string? cursor,int limit,CancellationToken t)
        {
            var id = await Folder(a,f,t); var offset = MailboxPaging.Offset(cursor);
            var page = await store.List(id,offset,limit,t);
            return new(page.Items.Select(m=>new MailboxItem(m.Id.ToString(),m.Subject,m.From,m.Date,m.IsRead)).ToArray(),page.HasMore?(offset+limit).ToString():null);
        }
        private async Task<Guid> Folder(MailboxIdentity a,string folder,CancellationToken t) =>
            (await ((IMailOrganizer)protocols).Folders(a.Id,t)).SingleOrDefault(f=>f.Id.ToString()==folder)?.Id ?? throw new InvalidDataException("目录不属于当前邮箱。");
        public async Task Read(MailboxIdentity a,string f,string item,Stream destination,CancellationToken t)
        {
            var folder=await Folder(a,f,t);
            using var db=store.Open(); using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT raw FROM messages WHERE id=$m AND folder_id=$f";
            cmd.Parameters.AddWithValue("$m",item); cmd.Parameters.AddWithValue("$f",folder.ToString());
            var raw=cmd.ExecuteScalar() as byte[] ?? throw new InvalidDataException("邮件已移走。");
            await destination.WriteAsync(raw,t);
        }
        public async Task<string> Upload(MailboxIdentity a,string f,Stream mime,CancellationToken t)
        {
            var folder=await Folder(a,f,t); using var output=new MemoryStream(); await mime.CopyToAsync(output,t);
            var raw=output.ToArray(); var parsed=await new MimeReader().Parse(raw,t);
            return (await new AvailableMailStore(store).Import(folder,raw,parsed,t)).ToString();
        }
        public async Task Mark(MailboxIdentity a,string f,string id,bool read,CancellationToken t)
        {
            var result=(await protocols.Mark(a.Id,Guid.Parse(f),[id],read?MailMark.Read:MailMark.Unread,"",t)).Single();
            if (!result.Success) throw new InvalidDataException(result.Detail);
        }
        public async Task<string> Move(MailboxIdentity a,string f,string id,string dest,CancellationToken t)
        {
            var result=(await protocols.Apply(new(a.Id,Guid.Parse(f),[id],MailAction.Move,Guid.Parse(dest)),t)).Single();
            return result.Success ? result.DestinationKey ?? id : throw new InvalidDataException(result.Detail);
        }
    }

    private sealed class Graph(SqliteMailStore store,GraphMailService graph) : IMailboxProvider
    {
        public MailboxType Type { get; } = new("outlook-com","个人 Outlook.com",MailboxFeatures.Read | MailboxFeatures.Mark | MailboxFeatures.Move);
        public Task Connect(MailboxIdentity a,CancellationToken t) => graph.SignIn(a.Id,t);
        public async Task<MailboxFolder[]> Folders(MailboxIdentity a,CancellationToken t)
        {
            var result=new List<MailboxFolder>();
            var parents=new Queue<string?>(); parents.Enqueue(null);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while(parents.TryDequeue(out var parent))
            {
                string? cursor=null;
                var pages = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    var page=await graph.Folders(a.Id,parent,cursor,t); cursor=page.Next;
                    if (cursor != null && !pages.Add(cursor)) throw new InvalidDataException("服务器重复返回同一目录页。");
                    foreach(var f in page.Items) if (visited.Add(f.Id)) { result.Add(new(f.Id,f.Name,"")); if(f.ChildCount>0)parents.Enqueue(f.Id); }
                } while(cursor!=null);
            }
            return result.ToArray();
        }
        public async Task<MailboxPageResult> List(MailboxIdentity a,string f,string? cursor,int limit,CancellationToken t)
        {
            var page=await graph.Messages(a.Id,f,cursor,limit,t);
            return new(page.Items.Select(m=>new MailboxItem(m.Id,m.Subject,m.From,m.Date,m.IsRead)).ToArray(),page.Next);
        }
        public async Task Read(MailboxIdentity a,string f,string id,Stream dest,CancellationToken t) =>
            await dest.WriteAsync(await store.Read(await graph.Download(a.Id,id,t),t),t);
        public Task Mark(MailboxIdentity a,string f,string id,bool read,CancellationToken t) => graph.MarkRead(a.Id,id,read,t);
        public Task<string> Move(MailboxIdentity a,string f,string id,string dest,CancellationToken t) => graph.Move(a.Id,id,dest,t);
    }

    private sealed class Imap(string type,ProtocolMailService protocols) : IMailboxProvider
    {
        public MailboxType Type {get;}=new(type,"IMAP",MailboxFeatures.Read);
        public Task Connect(MailboxIdentity a,CancellationToken t) => protocols.Folders(a.Id,t);
        public async Task<MailboxFolder[]> Folders(MailboxIdentity a,CancellationToken t) =>
            (await protocols.Folders(a.Id,t)).Where(f=>f.Selectable).Select(f=>new MailboxFolder(f.Path,f.Name,f.Role)).ToArray();
        public async Task<MailboxPageResult> List(MailboxIdentity a,string f,string? cursor,int limit,CancellationToken t)
        {
            var offset=MailboxPaging.Offset(cursor);var page=await protocols.Summaries(a.Id,f,offset,limit,t);
            return new(page.Items.Select(m=>new MailboxItem($"{page.UidValidity}/{m.Uid}",m.Subject,m.From,m.Date,m.IsRead)).ToArray(),page.HasMore?(offset+limit).ToString():null);
        }
        public Task Read(MailboxIdentity a,string f,string id,Stream dest,CancellationToken t) => protocols.CopyImapMessage(a.Id,f,id,dest,t);
    }
}

public sealed partial class ProtocolMailService
{
    internal async Task CopyImapMessage(Guid account,string path,string key,Stream destination,CancellationToken token)
    {
        var parts=key.Split('/');
        if(parts.Length!=2 || !uint.TryParse(parts[0],out var validity) || !uint.TryParse(parts[1],out var uid) || uid==0)throw new InvalidDataException("邮件引用无效。");
        await Account(account,AccountCapabilities.Folders,token);
        using var client=_imapClients(); await Connect(client,account,false,token);
        var folder=await client.GetFolderAsync(path,token);
        await folder.OpenAsync(FolderAccess.ReadOnly,token);
        if(folder.UidValidity!=validity)throw new InvalidDataException("目录标识已变化，请刷新列表。");
        using var stream=await folder.GetStreamAsync(new UniqueId(uid),0,MimeReader.MaxMessageBytes+1,token);
        if(stream.Length>MimeReader.MaxMessageBytes)throw new InvalidDataException("邮件超过 25 MiB 阅读限制。");
        await stream.CopyToAsync(destination,token); await client.DisconnectAsync(true,token);
    }
}
