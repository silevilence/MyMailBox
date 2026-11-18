using MailKit;
using MailKit.Net.Imap;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class ProtocolMailService
{
    public Task Undo(Guid operation, CancellationToken token = default) => Task.Run(() => _localActions.Undo(operation,token),token);

    public async Task<IReadOnlyList<MailActionResult>> Apply(MailActionRequest request, CancellationToken token = default)
    {
        if (!Enum.IsDefined(request.Action)) throw new InvalidDataException("未知邮件动作。");
        if (request.Keys.Length == 0) return [];
        var identity = await ManagedAccount(request.Account, token);
        if (IsImap(identity)) return await ImapActions(identity,request,token);
        await LocalFolder(identity.Id,request.Source,token);
        var destination = request.Action switch
        {
            MailAction.Trash => _localActions.Special(identity,MailAction.Trash),
            MailAction.Archive when request.Destination is null => _localActions.Special(identity,MailAction.Archive),
            _ => request.Destination
        };
        var results = new List<MailActionResult>();
        foreach (var key in request.Keys.Distinct())
        {
            token.ThrowIfCancellationRequested();
            try { results.Add(_localActions.Apply(request,key,destination,token)); }
            catch (InvalidDataException e) { results.Add(new(key,false,e.Message)); }
            catch (Microsoft.Data.Sqlite.SqliteException) { results.Add(new(key,false,"本地写入失败，事务已回滚。")); }
        }
        return results;
    }

    private Task<IReadOnlyList<MailActionResult>> ImapActions(MailAccount identity, MailActionRequest request, CancellationToken token) =>
        OnImap<IReadOnlyList<MailActionResult>>(identity.Id,async (client,cache) =>
        {
            var source = await VerifiedFolder(client,cache,request.Source,
                request.Action == MailAction.Copy ? FolderAccess.ReadOnly : FolderAccess.ReadWrite,token);
            IMailFolder? dest = null;
            Guid? targetId = request.Destination;
            if (request.Action != MailAction.DeletePermanently)
            {
                if (request.Action == MailAction.Trash || request.Action == MailAction.Archive && targetId is null)
                {
                    var special = request.Action == MailAction.Trash ? SpecialFolder.Trash : SpecialFolder.Archive;
                    dest = client.GetFolder(special) ?? throw new InvalidDataException("服务器未声明对应的垃圾箱或归档目录，请显式选择目标并使用移动。");
                    await dest.StatusAsync(StatusItems.UidValidity,token);
                    targetId = cache.Start(dest.FullName,dest.UidValidity,identity.Name).Id;
                }
                else
                {
                    var known = cache.Remote(targetId ?? throw new InvalidDataException("请选择目标目录。"));
                    dest = await client.GetFolderAsync(known.Path,token);
                    if (dest.FullName != source.FullName) await dest.StatusAsync(StatusItems.UidValidity,token);
                    if (dest.UidValidity != known.Validity) throw new InvalidDataException("目标目录已改变，请刷新后重新选择。");
                }
                if (dest.FullName == source.FullName) throw new InvalidDataException("目标与来源是同一目录。");
            }
            var results = new List<MailActionResult>();
            foreach (var key in request.Keys.Distinct())
            {
                token.ThrowIfCancellationRequested();
                bool copied = false, confirmed = false;
                try
                {
                    if (!uint.TryParse(key,out uint value) || value == 0) throw new InvalidDataException("邮件标识无效。");
                    var uid = new UniqueId(value);
                    if (!(await source.FetchAsync(new[] {uid},MessageSummaryItems.UniqueId,token)).Any(s=>s.UniqueId == uid))
                        throw new InvalidDataException("来源邮件已移出或删除。");
                    UniqueId? target = null;
                    string detail;
                    if (request.Action == MailAction.Copy)
                    {
                        target = await source.CopyToAsync(uid,dest!,token);
                        detail = "服务器已确认复制";
                    }
                    else if (request.Action != MailAction.DeletePermanently && client.Capabilities.HasFlag(ImapCapabilities.Move))
                    {
                        target = await source.MoveToAsync(uid,dest!,token);
                        detail = "服务器已确认原生移动";
                    }
                    else
                    {
                        if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                            throw new InvalidDataException("服务器不支持按指定 UID 清除来源，无法安全执行。");
                        if (dest is not null)
                        {
                            target = await source.CopyToAsync(uid,dest,token);
                            copied = true;
                            if (target is null) throw new InvalidDataException("已复制但服务器未提供目标 UID，来源已保留。");
                            await dest.OpenAsync(FolderAccess.ReadOnly,token);
                            var expected = cache.Remote(targetId!.Value);
                            if (dest.UidValidity != expected.Validity || !(await dest.FetchAsync(new[] {target.Value},MessageSummaryItems.UniqueId,token)).Any(s=>s.UniqueId == target.Value))
                                throw new InvalidDataException("未能核实目标副本，已停止清理来源。");
                            source = await VerifiedFolder(client,cache,request.Source,FolderAccess.ReadWrite,token);
                        }
                        await source.AddFlagsAsync(uid,MessageFlags.Deleted,true,token);
                        await source.ExpungeAsync(new[] {uid},token);
                        detail = dest is null ? "已永久删除指定邮件" : "已验证目标副本并清除指定来源";
                    }
                    confirmed = true;
                    RecordServerAction(request.Source,key,targetId,target?.Id.ToString(),request.Action);
                    results.Add(new(key,true,detail,target?.Id.ToString()));
                }
                catch (InvalidDataException e) { results.Add(new(key,false,e.Message)); }
                catch (Exception e) when (e is IOException or MailKit.ProtocolException or CommandException or ServiceNotConnectedException or FolderNotOpenException or Microsoft.Data.Sqlite.SqliteException)
                {
                    results.Add(new(key,false,confirmed ? "服务器操作已完成，本地缓存未更新，请重新同步。" :
                        copied ? "已建立目标副本，来源清理尚未确认。请核对两个目录。" : "服务器未确认操作，请核对目录、权限与配额。"));
                }
            }
            return results;
        },token);

    private void RecordServerAction(Guid source, string key, Guid? destination, string? target, MailAction action)
    {
        using var db = _store.Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.Parameters.AddWithValue("$source",source.ToString()); c.Parameters.AddWithValue("$key",key);
        if (target is not null && destination is not null)
        {
            c.Parameters.AddWithValue("$dest",destination.Value.ToString()); c.Parameters.AddWithValue("$target",target);
            c.CommandText = """
                INSERT OR REPLACE INTO mail_marks(folder,item,starred,labels)
                    SELECT $dest,$target,starred,labels FROM mail_marks WHERE folder=$source AND item=$key;
                INSERT OR IGNORE INTO imap_summaries(folder,uid,subject,sender,size,date_ms,flags,envelope)
                    SELECT $dest,$target,subject,sender,size,date_ms,flags,envelope FROM imap_summaries WHERE folder=$source AND uid=$key;
                """;
            c.ExecuteNonQuery();
            if (action != MailAction.Copy)
            {
                c.CommandText = """
                    UPDATE messages SET folder_id=$dest WHERE id=(SELECT message FROM imap_summaries WHERE folder=$source AND uid=$key);
                    UPDATE imap_summaries SET message=COALESCE(message,(SELECT message FROM imap_summaries WHERE folder=$source AND uid=$key))
                        WHERE folder=$dest AND uid=$target;
                    """;
                c.ExecuteNonQuery();
            }
        }
        if (action != MailAction.Copy)
        {
            if (action == MailAction.DeletePermanently)
            {
                c.CommandText = "DELETE FROM messages WHERE id=(SELECT message FROM imap_summaries WHERE folder=$source AND uid=$key)";
                c.ExecuteNonQuery();
            }
            c.CommandText = "DELETE FROM mail_marks WHERE folder=$source AND item=$key; DELETE FROM imap_summaries WHERE folder=$source AND uid=$key";
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
