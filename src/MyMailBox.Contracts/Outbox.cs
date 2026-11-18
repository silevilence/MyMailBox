namespace MyMailBox.Contracts;

public enum MissedSend { SendWhenAvailable, HoldForReview }
public sealed record SendPlan(DateTimeOffset Due,string Zone,MissedSend Missed,int Revision,int DraftVersion);
public sealed record OutboxItem(Guid Id, Guid Account, string Subject, string State, string Detail,
    string[] Accepted, string[] Remaining, DateTimeOffset Created, bool CopyPending);

public interface IOutboxService
{
    Task<SendPlan?> GetSendPlan(Guid draft,CancellationToken token=default);
    Task Schedule(Guid draft,Guid account,TextMail message,SendPlan plan,CancellationToken token=default);
    Task Enqueue(Guid id, Guid account, TextMail message, CancellationToken cancellation = default);
    Task<SendOutcome> Deliver(Guid id, CancellationToken cancellation = default);
    Task<IReadOnlyList<OutboxItem>> List(Guid account, int offset, int limit, CancellationToken cancellation = default);
    Task<IReadOnlyList<string>> History(Guid id, CancellationToken cancellation = default);
    Task Cancel(Guid id, CancellationToken cancellation = default);
    Task Retry(Guid id, CancellationToken cancellation = default);
    Task SaveCopy(Guid id, CancellationToken cancellation = default);
}
