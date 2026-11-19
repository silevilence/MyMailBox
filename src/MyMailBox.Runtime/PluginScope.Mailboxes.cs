using MyMailBox.Contracts;

namespace MyMailBox.Runtime;

public sealed partial class PluginScope
{
    public IDisposable AddMailboxProvider(IMailboxProvider provider)
    {
        var type = provider.Type;
        if (!System.Text.RegularExpressions.Regex.IsMatch(type.Id, "^[a-z][a-z0-9-]{0,63}$"))
            throw new InvalidDataException("邮箱提供者类型 ID 无效。");
        var id = builtin ? type.Id : InstanceId + "/" + type.Id;
        return Own(pages.AddMailboxProvider(new ScopedMailbox(this, Target(provider), type with { Id = id })));
    }

    private sealed class ScopedMailbox(PluginScope owner, ServiceTarget<IMailboxProvider> target, MailboxType type) : IMailboxProvider
    {
        public MailboxType Type => type;
        private Task<T> Call<T>(MailboxIdentity account, MailboxFeatures feature, Func<IMailboxProvider,CancellationToken,Task<T>> action, CancellationToken token) =>
            owner.Run(t =>
            {
                if (!type.Features.HasFlag(feature)) throw new NotSupportedException("此邮箱不支持所选操作。");
                owner.CheckMailboxGrant(feature, account.Id);
                return action(target.Value, t);
            }, token);
        private async Task Call(MailboxIdentity account, MailboxFeatures feature, Func<IMailboxProvider,CancellationToken,Task> action, CancellationToken token) =>
            await Call(account, feature, async (p,t) => { await action(p,t); return true; }, token);
        public Task Connect(MailboxIdentity a, CancellationToken t) => Call(a,MailboxFeatures.Read,(p,c)=>p.Connect(a,c),t);
        public Task<MailboxFolder[]> Folders(MailboxIdentity a, CancellationToken t) => Call(a,MailboxFeatures.Read,(p,c)=>p.Folders(a,c),t);
        public Task<MailboxPageResult> List(MailboxIdentity a,string f,string? cursor,int n,CancellationToken t) => Call(a,MailboxFeatures.Read,(p,c)=>p.List(a,f,cursor,n,c),t);
        public Task Read(MailboxIdentity a,string f,string id,Stream dest,CancellationToken t) => Call(a,MailboxFeatures.Read,(p,c)=>p.Read(a,f,id,dest,c),t);
        public Task<MailboxDelta> Delta(MailboxIdentity a,string f,string? cursor,int n,CancellationToken t) => Call(a,MailboxFeatures.Delta,(p,c)=>p.Delta(a,f,cursor,n,c),t);
        public Task<SendOutcome> Send(MailboxIdentity a,Guid op,Stream mime,string[] envelope,CancellationToken t) => Call(a,MailboxFeatures.Send,(p,c)=>p.Send(a,op,mime,envelope,c),t);
        public Task<string> Upload(MailboxIdentity a,string f,Stream mime,CancellationToken t) => Call(a,MailboxFeatures.Upload,(p,c)=>p.Upload(a,f,mime,c),t);
        public Task<string> Move(MailboxIdentity a,string f,string id,string dest,CancellationToken t) => Call(a,MailboxFeatures.Move,(p,c)=>p.Move(a,f,id,dest,c),t);
        public Task Mark(MailboxIdentity a,string f,string id,bool read,CancellationToken t) => Call(a,MailboxFeatures.Mark,(p,c)=>p.Mark(a,f,id,read,c),t);
        public Task<MailboxQuota> Quota(MailboxIdentity a,CancellationToken t) => Call(a,MailboxFeatures.Quota,(p,c)=>p.Quota(a,c),t);
        public Task<MailboxPageResult> Search(MailboxIdentity a,string f,string query,string? cursor,int n,CancellationToken t) => Call(a,MailboxFeatures.Search,(p,c)=>p.Search(a,f,query,cursor,n,c),t);
    }

    private void CheckMailboxGrant(MailboxFeatures feature, Guid account)
    {
        if (builtin) return;
        var capability = feature switch {
            MailboxFeatures.Send => "mail.send",
            MailboxFeatures.Upload or MailboxFeatures.Move or MailboxFeatures.Mark => "mail.write",
            _ => "mail.read"
        };
        permissions.Require(capability, account.ToString());
    }
}
