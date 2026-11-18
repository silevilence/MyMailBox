using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ActivityRunner:IDisposable
{
    private readonly CancellationTokenSource _stop=new();private readonly Task _loop;
    public ActivityRunner(ActivityCenter center,Func<bool> locked,Func<IReadOnlyList<NotificationPresenter>> presenters)
    {
        _loop=Task.Run(async()=>
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
            try{while(await timer.WaitForNextTickAsync(_stop.Token)){try{await center.Dispatch(locked,presenters,_stop.Token);}catch(Microsoft.Data.Sqlite.SqliteException e)when(e.SqliteErrorCode is 5 or 6){}}}
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        });
    }
    public void Dispose(){_stop.Cancel();_loop.GetAwaiter().GetResult();_stop.Dispose();}
}
