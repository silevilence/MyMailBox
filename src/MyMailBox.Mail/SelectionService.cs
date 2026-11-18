using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class SelectionService : ISelectionService
{
    private readonly SqliteMailStore _store;
    private readonly IAccountService _accounts;
    public SelectionService(SqliteMailStore store, IAccountService accounts)
    {
        _store = store; _accounts = accounts;
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS selections(slot TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS selection_recent(reference TEXT PRIMARY KEY, used_at TEXT NOT NULL);"; command.ExecuteNonQuery();
    }
    public async Task<IReadOnlyList<SelectionChoice>> Query(SelectionQuery query, CancellationToken cancellation = default)
    {
        var accounts = await _accounts.List(cancellation); var folders = await _store.Folders(cancellation);
        var recent = await Task.Run(() =>
        {
            using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT reference FROM selection_recent ORDER BY used_at DESC LIMIT 20";
            using var reader = command.ExecuteReader(); var items = new HashSet<string>(); while (reader.Read()) items.Add(reader.GetString(0)); return items;
        }, cancellation);
        var result = new List<SelectionChoice>();
        foreach (var account in accounts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (query.Tag is { Length: > 0 } tag && !account.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            var required = query.Required | (query.Kind == SelectionKind.Identity ? AccountCapabilities.Send : AccountCapabilities.None);
            var available = account.Enabled && (account.Capabilities & required) == required;
            var reason = !account.Enabled ? "账户已停用" : !available ? "不具备所需能力：" + CapabilityName(required) : "";
            if (query.Kind == SelectionKind.Folder)
            {
                foreach (var folder in folders.Where(f => f.AccountId == account.Id && f.Role != "imap-detached"))
                {
                    var path = account.Name + " / " + string.Join(" / ", folder.Path.Split(" / ").Skip(1));
                    Add(new(SelectionKind.Folder, account.Id, folder.Id), folder.Name, path, folder.Role, folder.ParentId);
                }
            }
            else Add(new(query.Kind, account.Id), account.Name, account.Name, "", null);

            void Add(SelectionReference reference, string name, string path, string role, Guid? parent)
            {
                var wasRecent = recent.Contains(JsonSerializer.Serialize(reference));
                if (query.RecentOnly && !wasRecent) return;
                var searchable = $"{account.Name} {name} {account.Address} {account.Provider} {account.Alias} {path} {string.Join(' ', account.Tags)}";
                if (!searchable.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase)) return;
                result.Add(new(reference, name, account.Address, account.Provider, account.Color, account.Tags, path, role, parent, available, reason, wasRecent));
            }
        }
        return result.OrderByDescending(c => c.Recent).ThenBy(c => c.Path).ToArray();
    }

    public async Task Save(string slot, SavedSelection selection, CancellationToken cancellation = default)
    {
        if (selection.Kind == SelectionKind.Identity && (selection.DynamicTags || selection.References.Length != 1)) throw new InvalidDataException("发送身份必须是一个固定选择。");
        if (selection.DynamicTags && selection.Tags.Length == 0) throw new InvalidDataException("动态集合至少需要一个标签。");
        if (!selection.DynamicTags && selection.References.Length == 0) throw new InvalidDataException("请选择至少一个目标。");
        if (selection.References.Any(r => r.Kind != selection.Kind)) throw new InvalidDataException("选择类型不一致。");
        var resolved = await Resolve(selection, cancellation);
        if (resolved.Missing.Length > 0) throw new InvalidDataException("部分引用已删除，请重新选择。");
        if (resolved.Choices.Any(c => !c.Available)) throw new InvalidDataException(string.Join("；", resolved.Choices.Where(c => !c.Available).Select(c => c.Path + "：" + c.Reason)));
        await Task.Run(() =>
        {
            using var db = _store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO selections VALUES($slot,$value) ON CONFLICT(slot) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$slot", slot); command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(selection)); command.ExecuteNonQuery();
            foreach (var item in resolved.Choices)
            {
                command.Parameters.Clear(); command.CommandText = "INSERT INTO selection_recent VALUES($ref,$at) ON CONFLICT(reference) DO UPDATE SET used_at=excluded.used_at";
                command.Parameters.AddWithValue("$ref", JsonSerializer.Serialize(item.Reference)); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
            }
            cancellation.ThrowIfCancellationRequested(); transaction.Commit();
        }, cancellation);
    }

    public Task<SavedSelection?> Load(string slot, CancellationToken cancellation = default) => Task.Run(() =>
    {
        using var db = _store.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT value FROM selections WHERE slot=$slot"; command.Parameters.AddWithValue("$slot", slot);
        return command.ExecuteScalar() is string value ? JsonSerializer.Deserialize<SavedSelection>(value) : null;
    }, cancellation);

    public async Task<ResolvedSelection> Resolve(SavedSelection selection, CancellationToken cancellation = default)
    {
        var choices = await Query(new(selection.Kind, selection.Required), cancellation);
        if (selection.DynamicTags) return new(choices.Where(c => selection.Tags.Any(t => c.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))).ToArray(), []);
        return new(choices.Where(c => selection.References.Contains(c.Reference)).ToArray(), selection.References.Where(r => !choices.Any(c => c.Reference == r)).ToArray());
    }
    public static string CapabilityName(AccountCapabilities capability) => string.Join("、", new[] { (AccountCapabilities.Receive, "接收"), (AccountCapabilities.Send, "发送"), (AccountCapabilities.Folders, "目录"), (AccountCapabilities.Import, "导入") }.Where(p => capability.HasFlag(p.Item1)).Select(p => p.Item2));
}
