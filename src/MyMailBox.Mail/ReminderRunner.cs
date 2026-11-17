using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ReminderRunner : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public ReminderRunner(IReminderService scheduler, Action<string>? failed = null) : this(scheduler.Tick, failed) { }
    public ReminderRunner(Func<CancellationToken, Task<int>> tick, Action<string>? failed = null)
    {
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    try { await tick(_stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception e) { failed?.Invoke("提醒检查未完成：" + e.Message); }
                } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }
    public void Dispose() { _stop.Cancel(); _loop.GetAwaiter().GetResult(); _stop.Dispose(); }
}
