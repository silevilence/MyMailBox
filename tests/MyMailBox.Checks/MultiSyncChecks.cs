using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Collections.Concurrent;
using System.Net.Sockets;

internal static class MultiSyncChecks
{
    private sealed class Fixture(AccountService accounts) : IReceiveProvider
    {
        public readonly ConcurrentDictionary<Guid, int> Calls = new();
        public int Peak;
        private int _active;
        public bool Supports(MailAccount account) => true;
        public async Task Receive(MailAccount account, int range, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            lock (Calls) Peak = Math.Max(Peak, active);
            try
            {
                Calls.AddOrUpdate(account.Id, 1, (_, n) => n + 1);
                await Task.Delay(80, token);
                if (account.Name == "offline") throw new SocketException((int)SocketError.NetworkUnreachable);
                if (account.Name == "expired") { await accounts.AuthenticationFailed(account.Id); throw new InvalidDataException("expired"); }
                MailChecks.Check(range == 200, "Stored receive range was lost");
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task<IReadOnlyList<InboxMessage>> Inbox(MailAccount account, InboxPosition? before, int limit, CancellationToken token)
        {
            var key = account.Id.ToString();
            return Task.FromResult<IReadOnlyList<InboxMessage>>(before is null || string.CompareOrdinal(key,before.Key)<0 ?
                [new(account.Id, account.Name, new(1,key), "主题", "fixture", true)] : []);
        }
        public Task<string> Open(MailAccount account, string key, CancellationToken token) => Task.FromResult(account.Name);
    }

    public static async Task Run()
    {
        var store = new SqliteMailStore(Path.Combine(PluginChecks.Repository, "artifacts/checks/multi-" + Guid.NewGuid().ToString("N")));
        var accounts = new AccountService(store);
        var provider = new Fixture(accounts);
        var service = new UnifiedInboxService(store, accounts, provider);
        var ids = new List<Guid>();
        for (int i = 0; i < 10; i++)
        {
            var name = i switch { 0 => "offline", 1 => "expired", _ => "账户 " + i };
            var account = await accounts.Save(new(null, name, "imap", $"fixture{i}@example.test"));
            ids.Add(account.Id); await service.Configure(account.Id, true, 200);
        }
        var sendOnly = await accounts.Save(new(null, "仅发", "smtp", "send@example.test"));
        var disabled = await accounts.Save(new(null, "即将停用", "imap", "disabled@example.test"));
        await service.Configure(disabled.Id, true, 200);
        await accounts.Save(new(disabled.Id, disabled.Name, disabled.Provider, disabled.Address, Enabled: false));
        var restarted = new UnifiedInboxService(store, accounts, provider);
        using var runner = new ReceiveRunner(restarted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        IReadOnlyList<ReceiveStatus> rows;
        while ((rows = await restarted.Status()).Count(r => r.State is "offline" or "auth" or "ready") < 10)
            await Task.Delay(50, timeout.Token);
        MailChecks.Check(provider.Calls.Count == 10 && provider.Peak is >= 2 and <= 3, "Concurrency limit or account eligibility failed");
        MailChecks.Check(rows.Count(r => r.State == "ready" && r.LastSuccess.HasValue) == 8 && rows.Any(r => r.State == "offline") &&
            rows.Any(r => r.State == "auth") && rows.Single(r => r.Account == sendOnly.Id).Available == false, "Failure isolation or status classification failed");
        await restarted.Configure(ids[2], false, 200);
        MailChecks.Check((await restarted.Status()).Single(r => r.Account == ids[2]).State == "paused", "Pause not visible");
        await restarted.Refresh(ids[2]);
        while ((await restarted.Status()).Single(r => r.Account == ids[2]).State != "ready") await Task.Delay(50, timeout.Token);
        MailChecks.Check(provider.Calls[ids[2]] == 2, "Manual refresh while paused failed");
        var first = await service.Inbox(null, null, 6);
        var rest = await service.Inbox(null, first.Messages[^1].Position, 6);
        MailChecks.Check(first.HasMore && !rest.HasMore && first.Messages.Concat(rest.Messages).Distinct().Count() == 10, "Unified pagination failed");
        var filtered = await service.Inbox(ids[4], null, 10);
        MailChecks.Check(filtered.Messages.Single().Account == ids[4] && await service.Open(ids[4], filtered.Messages[0].Position.Key) == "账户 4", "Provider-independent filter/open failed");
        Console.WriteLine("PASS multi-sync: ten accounts, bounded concurrency, pause/manual refresh, disabled/send-only exclusion, offline/auth isolation, unified keyset paging and provider replacement");
    }
}
