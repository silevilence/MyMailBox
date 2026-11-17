using MyMailBox.Contracts;

namespace MyMailBox.Mail;

/// <summary>UI access respects closed/removed accounts; maintenance keeps access to retained archives.</summary>
public sealed class AvailableMailStore(SqliteMailStore store) : IMailStore
{
    private readonly AccountService _accounts = new(store);
    public async Task<IReadOnlyList<MailFolder>> Folders(CancellationToken cancellation = default)
    {
        var accounts = await _accounts.List(cancellation); var active = accounts.Where(a => a.Enabled).ToDictionary(a => a.Id);
        return (await store.Folders(cancellation)).Where(f => active.ContainsKey(f.AccountId))
            .Select(f => f with { Path = active[f.AccountId].Name + " / " + string.Join(" / ", f.Path.Split(" / ").Skip(1)) }).ToArray();
    }
    private async Task RequireFolder(Guid folder, CancellationToken cancellation)
    { if (!(await Folders(cancellation)).Any(f => f.Id == folder)) throw new InvalidOperationException("邮箱已关闭、移除或目录不存在；请打开邮箱并刷新。"); }
    public async Task<MailPageResult> List(Guid folderId, int offset, int limit, CancellationToken cancellation = default)
    { await RequireFolder(folderId, cancellation); return await store.List(folderId, offset, limit, cancellation); }
    public async Task<Guid> Import(Guid folderId, byte[] raw, ParsedMail metadata, CancellationToken cancellation = default)
    { await RequireFolder(folderId, cancellation); return await store.Import(folderId, raw, metadata, cancellation); }
    public async Task<byte[]> Read(Guid messageId, CancellationToken cancellation = default)
    {
        var folder = await Task.Run(() =>
        {
            using var db = store.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT folder_id FROM messages WHERE id=$id"; command.Parameters.AddWithValue("$id", messageId.ToString());
            return command.ExecuteScalar() as string ?? throw new FileNotFoundException("邮件已不存在。");
        }, cancellation);
        await RequireFolder(Guid.Parse(folder), cancellation); return await store.Read(messageId, cancellation);
    }
}
