namespace MyMailBox.Contracts;

public sealed record ActivityEntry(long Id,string Kind,Guid? Account,string Title,string Detail,string Route,string Reference,Guid? Folder,DateTimeOffset At);
public sealed record NoticePolicy(bool Quiet,bool HideWhileLocked,string Presenter);
public sealed record ActivityBatch(long Last,string Kind,int Count,string Text);
public sealed record NotificationPresenter(string Id,string Name,Func<ActivityBatch,CancellationToken,Task> Show);
public sealed record ActivityLink(string Route,Guid? Account,Guid? Record,SearchHit? Mail=null);
public interface IActivityCenter
{
    Task<IReadOnlyList<ActivityEntry>> Recent(int offset=0,int limit=50,CancellationToken token=default);
    Task<NoticePolicy> Policy(CancellationToken token=default);
    Task Configure(NoticePolicy settings,CancellationToken token=default);
    Task<bool> Enabled(string kind,Guid? account,CancellationToken token=default);
    Task SetEnabled(string kind,Guid? account,bool enabled,CancellationToken token=default);
    Task<ActivityLink> Resolve(long entry,CancellationToken token=default);
}
public sealed record ActivityPage(IActivityCenter Center,IAccountService Accounts);
