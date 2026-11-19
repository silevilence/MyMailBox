using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Collections.Concurrent;

namespace MyMailBox.Mail;

public sealed class UnifiedInboxService : IUnifiedInbox
{
    private readonly SqliteMailStore _store;
    private readonly IAccountService _accounts;
    private readonly IReceiveProvider[] _providers;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    public UnifiedInboxService(SqliteMailStore store, IAccountService accounts, params IReceiveProvider[] providers)
    {
        _store = store; _accounts = accounts; _providers = providers;
        using var db = store.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS receive_jobs(
                account TEXT PRIMARY KEY REFERENCES accounts(id),automatic INTEGER NOT NULL DEFAULT 0,
                scope INTEGER NOT NULL DEFAULT 50,requested INTEGER NOT NULL DEFAULT 0,due INTEGER NOT NULL DEFAULT 0,
                revision INTEGER NOT NULL DEFAULT 0,state TEXT NOT NULL DEFAULT 'paused',detail TEXT NOT NULL DEFAULT '',
                last_ok INTEGER);
            """;
        command.ExecuteNonQuery();
    }

    private IReceiveProvider? Provider(MailAccount account, bool cached = false) => account.Enabled && account.Capabilities.HasFlag(AccountCapabilities.Receive)
        ? _providers.FirstOrDefault(p => cached ? p.CanReadCache(account) : p.Supports(account)) : null;

    private SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (name,value) in parameters) command.Parameters.AddWithValue("$" + name, value);
        return command;
    }

    private async Task<(MailAccount Account, IReceiveProvider Provider)> Resolve(Guid id, CancellationToken token, bool cached = false)
    {
        var account = (await _accounts.List(token)).FirstOrDefault(a => a.Id == id);
        if (account is null || Provider(account, cached) is not { } provider) throw new InvalidDataException("账户已停用、移除或尚无可用的接收提供者。");
        return (account, provider);
    }

    public async Task<IReadOnlyList<ReceiveStatus>> Status(CancellationToken token = default)
    {
        var accounts = await _accounts.List(token);
        var rows = new List<ReceiveStatus>();
        using var db = _store.Open();
        using var query = Command(db, "SELECT automatic,scope,state,detail,last_ok FROM receive_jobs WHERE account=$account");
        var key = query.Parameters.Add("$account", SqliteType.Text);
        foreach (var account in accounts.Where(a => a.Provider != "local"))
        {
            key.Value = account.Id.ToString();
            using var r = query.ExecuteReader();
            var exists = r.Read();
            var usable = Provider(account) is not null;
            var reason = !account.Enabled ? "账户已停用" : !account.Capabilities.HasFlag(AccountCapabilities.Receive) ? "仅发账户" : "此提供者尚未提供后台接收";
            rows.Add(new(account.Id, account.Name, usable, exists && r.GetBoolean(0), exists ? r.GetInt32(1) : 50,
                usable ? exists ? r.GetString(2) : "paused" : "unavailable",
                usable ? exists ? r.GetString(3) : "可手动接收或开启自动接收。" : reason,
                exists && !r.IsDBNull(4) ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)) : null));
        }
        return rows;
    }

    public async Task Configure(Guid account, bool automatic, int range, CancellationToken token = default)
    {
        if (range is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(range));
        await Resolve(account, token);
        using var db = _store.Open();
        using var cmd = Command(db, """
            INSERT INTO receive_jobs(account,automatic,scope,state) VALUES($id,$auto,$scope,$state)
            ON CONFLICT(account) DO UPDATE SET automatic=$auto,scope=$scope,requested=0,due=0,
                state=$state,detail='',revision=revision+1
            """, ("id", account.ToString()), ("auto", automatic), ("scope", range), ("state", automatic ? "queued" : "paused"));
        cmd.ExecuteNonQuery();
        if (_active.TryGetValue(account, out var running))
            try { await running.CancelAsync(); } catch (ObjectDisposedException) { }
    }

    public async Task Refresh(Guid account, CancellationToken token = default)
    {
        await Resolve(account, token);
        using var db = _store.Open();
        using var cmd = Command(db, """
            INSERT INTO receive_jobs(account,requested,state) VALUES($id,1,'queued')
            ON CONFLICT(account) DO UPDATE SET requested=1,due=0,state='queued',detail='',revision=revision+1
            WHERE state NOT IN ('running','queued') AND (state<>'limited' OR due<=unixepoch())
            """, ("id", account.ToString()));
        cmd.ExecuteNonQuery();
    }

    public async Task<UnifiedPage> Inbox(Guid? account, InboxPosition? before, int limit, CancellationToken token = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var merged = new List<InboxMessage>();
        foreach (var identity in await _accounts.List(token))
        {
            if ((account is null || identity.Id == account) && Provider(identity, cached: true) is { } provider)
                merged.AddRange(await provider.Inbox(identity, before, limit + 1, token));
        }
        var rows = merged.OrderByDescending(m => m.Position.UnixMilliseconds).ThenByDescending(m => m.Position.Key, StringComparer.Ordinal)
            .Take(limit + 1).ToArray();
        return new(rows.Take(limit).ToArray(), rows.Length > limit);
    }

    public async Task<string> Open(Guid account, string key, CancellationToken token = default)
    {
        var (identity, provider) = await Resolve(account, token, cached: true);
        return await provider.Open(identity, key, token);
    }

    internal async Task Tick(CancellationToken token)
    {
        var due = new List<(Guid Id, int Range, int Revision)>();
        using (var db = _store.Open())
        using (var cmd = Command(db, """
            SELECT j.account,j.scope,j.revision FROM receive_jobs j JOIN accounts a ON a.id=j.account
            WHERE a.enabled AND NOT a.removed AND (a.capabilities & 1)<>0 AND (j.requested OR j.automatic)
                AND j.due<=unixepoch() ORDER BY j.due,j.account
            """))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) due.Add((Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2)));
        if (due.Count == 0) return;
        FileStream lease;
        try { lease = new FileStream(_store.DatabasePath + ".receive.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return; }
        using (lease)
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token },
                async (job, t) => await Receive(job.Id, job.Range, job.Revision, t));
    }

    private async Task Receive(Guid id, int range, int revision, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!_active.TryAdd(id, operation)) return;
        try
        {
            using (var db = _store.Open())
            using (var cmd = Command(db, """
                UPDATE receive_jobs SET state='running',detail='正在接收邮件。' WHERE account=$id AND revision=$revision
                    AND (requested OR automatic) AND due<=unixepoch()
                    AND EXISTS(SELECT 1 FROM accounts WHERE id=$id AND enabled AND NOT removed AND (capabilities & 1)<>0)
                """, ("id", id.ToString()), ("revision", revision)))
                if (cmd.ExecuteNonQuery() == 0) return;

            var (identity, provider) = await Resolve(id, operation.Token);
            await provider.Receive(identity, range, operation.Token);
            Finish(id, revision, "ready", "收件箱已更新。", true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception e)
        {
            var kind = MailboxFailure.Kind(e);
            if (kind == null && (e is System.Net.Sockets.SocketException or HttpRequestException or TimeoutException or OperationCanceledException || e.Data.Contains("receive-offline")))
                kind = MailboxFailureKind.Offline;
            if (e is SqliteException { SqliteErrorCode: 13 }) kind = MailboxFailureKind.Storage;
            if (kind == null && (await _accounts.List(token)).FirstOrDefault(a => a.Id == id)?.AuthenticationState.Contains("授权失败") == true)
                kind = MailboxFailureKind.Authorization;
            var (state, detail) = kind switch
            {
                MailboxFailureKind.Offline => ("offline", "无法连接服务器。离线副本可继续阅读，网络恢复后可以立即刷新。"),
                MailboxFailureKind.Authorization => ("auth", "授权失败或已过期。请在此账户的连接页面重新授权后刷新。"),
                MailboxFailureKind.Throttled => ("limited", "服务方暂时限制请求，请在等待期结束后刷新。"),
                MailboxFailureKind.PluginPermission => ("permission", "请在插件设置中核对该邮箱实例的收件权限，再刷新。"),
                MailboxFailureKind.Policy => ("policy", "组织策略阻止接收。请联系管理员确认设备准入和账户权限。"),
                MailboxFailureKind.Storage => ("storage", "工作空间磁盘空间不足。请释放空间后刷新，将从已提交的进度继续。"),
                _ => ("failed", "接收失败。请检查提供者插件、服务器配置和本地存储后刷新。")
            };
            var retry = MailboxFailure.RetryAfter(e);
            if (retry.HasValue) detail += $" 可重试时间：{retry.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}。";
            Finish(id, revision, state, detail, false, retry);
        }
        finally { _active.TryRemove(id, out _); }
    }

    private void Finish(Guid id, int revision, string state, string detail, bool success, DateTimeOffset? retry = null)
    {
        using var db = _store.Open();
        using var tx=db.BeginTransaction();using var cmd = Command(db, """
            UPDATE receive_jobs SET state=$state,detail=$detail,requested=0,due=$due,
                last_ok=CASE WHEN $ok THEN unixepoch() ELSE last_ok END WHERE account=$id AND revision=$revision
            """, ("id", id.ToString()), ("revision", revision), ("state", state), ("detail", detail), ("ok", success), ("due", (retry ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToUnixTimeSeconds()));
        cmd.Transaction=tx;
        if(cmd.ExecuteNonQuery()==1)ActivityCenter.Add(cmd,"receive/"+Guid.NewGuid(),success?"sync":"error",id.ToString(),success?"收件箱已同步":"收件未完成",detail,"account",id.ToString());
        tx.Commit();
    }
}

public sealed class ReceiveRunner : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _work;
    public ReceiveRunner(UnifiedInboxService inbox, Action<string>? notify = null)
    {
        _work = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                do
                {
                    try { await inbox.Tick(_stop.Token); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception) { notify?.Invoke("接收队列未完成，请检查本地工作空间。"); }
                } while (await timer.WaitForNextTickAsync(_stop.Token));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }
    public void Dispose() { _stop.Cancel(); _work.GetAwaiter().GetResult(); _stop.Dispose(); }
}
