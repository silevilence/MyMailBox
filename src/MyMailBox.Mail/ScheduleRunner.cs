using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class ScheduleRunner : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public ScheduleRunner(IScheduleService service, Action<string>? failed = null)
    {
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); var pending = new List<Task>();
            try
            {
                do
                {
                    pending.RemoveAll(t => t.IsCompleted);
                    if (pending.Count < 32) pending.Add(Check());
                } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(pending).ConfigureAwait(false); }
            async Task Check()
            {
                try { await service.Tick(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception error) { failed?.Invoke("调度检查未完成：" + error.Message); }
            }
        });
    }
    public void Dispose() { _stop.Cancel(); _loop.GetAwaiter().GetResult(); _stop.Dispose(); }
}
