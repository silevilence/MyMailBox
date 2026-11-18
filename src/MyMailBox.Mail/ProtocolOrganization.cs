using MailKit;
using MailKit.Net.Imap;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class ProtocolMailService
{
    private readonly OrganizationStore _organization;
    private static bool IsImap(MailAccount account) => account.Provider is "imap" or "imap-smtp";
    private async Task<MailAccount> ManagedAccount(Guid id, CancellationToken token)
    {
        var account = (await _accounts.List(token)).SingleOrDefault(a => a.Id == id && a.Enabled);
        if (account is null || account.Provider != "local" && !(account.Capabilities.HasFlag(AccountCapabilities.Receive) &&
            (IsImap(account) || account.Provider == "pop3"))) throw new InvalidDataException("该账户已停用或不提供目录整理。");
        return account;
    }

    private async Task<MyMailBox.Contracts.MailFolder> LocalFolder(Guid account, Guid folder, CancellationToken token) =>
        (await _store.Folders(token)).FirstOrDefault(f => f.AccountId == account && f.Id == folder)
        ?? throw new InvalidDataException("目录引用已失效，请重新选择目录。");

    private Task<T> OnImap<T>(Guid account, Func<ImapClient,ImapCache,Task<T>> work, CancellationToken token) =>
        ReadOperation(account, async () =>
        {
            using var client = _imapClients();
            var endpoint = await Connect(client, account, false, token);
            return await work(client, new ImapCache(_store, account, endpoint));
        });

    private static async Task<IMailFolder> VerifiedFolder(ImapClient client, ImapCache cache, Guid id, FolderAccess access, CancellationToken token)
    {
        var known = cache.Remote(id);
        var folder = await client.GetFolderAsync(known.Path, token);
        await folder.OpenAsync(access, token);
        if (folder.UidValidity != known.Validity) throw new InvalidDataException("目录标识已改变。请刷新目录并重新选择。");
        if (access == FolderAccess.ReadWrite && folder.Access != FolderAccess.ReadWrite)
            throw new InvalidDataException("服务器仅允许读取此目录。");
        return folder;
    }

    async Task<IReadOnlyList<ManagedFolder>> IMailOrganizer.Folders(Guid account, CancellationToken token)
    {
        var identity = await ManagedAccount(account, token);
        if (!IsImap(identity))
            return (await _store.Folders(token)).Where(f => f.AccountId == account)
                .Select(f => new ManagedFolder(f.Id, f.Name, f.Path, identity.Provider == "local" && OrganizationStore.CanRename(f), f.Role)).ToArray();
        return await OnImap<IReadOnlyList<ManagedFolder>>(account, async (client,cache) =>
        {
            var folders = new Dictionary<string,IMailFolder>(StringComparer.Ordinal);
            foreach (var ns in client.PersonalNamespaces)
                foreach (var f in await client.GetFoldersAsync(ns, StatusItems.UidValidity, cancellationToken: token))
                    if (!f.Attributes.HasFlag(FolderAttributes.NoSelect)) folders[f.FullName] = f;
            folders.TryAdd(client.Inbox.FullName, client.Inbox);
            var results = new List<ManagedFolder>();
            foreach (var remote in folders.Values.OrderBy(f => f.FullName))
            {
                if (remote.UidValidity == 0) await remote.StatusAsync(StatusItems.UidValidity, token);
                if (remote.UidValidity == 0) await remote.OpenAsync(FolderAccess.ReadOnly, token);
                var local = cache.Start(remote.FullName, remote.UidValidity, identity.Name);
                var role = Role(remote.Attributes);
                results.Add(new(local.Id, remote.Name, remote.FullName, role == "custom" && !remote.IsNamespace, role));
            }
            foreach (var removed in cache.Paths().Except(folders.Keys, StringComparer.Ordinal)) cache.Detach(removed);
            return results;
        }, token);
    }

    public async Task<Guid> Create(Guid account, Guid? parent, string name, CancellationToken token = default)
    {
        name = OrganizationStore.FolderName(name);
        var identity = await ManagedAccount(account, token);
        if (identity.Provider == "pop3") throw new InvalidDataException("POP3 不支持服务器目录管理。");
        if (!IsImap(identity))
            return (await new LocalMailboxService(_store, _accounts, new MimeReader()).CreateFolder(account, parent, name, token)).Id;
        return await OnImap(account, async (client,cache) =>
        {
            var container = parent.HasValue ? await VerifiedFolder(client, cache, parent.Value, FolderAccess.ReadOnly, token)
                : client.GetFolder(client.PersonalNamespaces[0]);
            var created = await container.CreateAsync(name, true, token) ?? throw new InvalidDataException("服务器未返回新建目录。");
            await created.OpenAsync(FolderAccess.ReadOnly, token);
            return cache.Start(created.FullName, created.UidValidity, identity.Name).Id;
        }, token);
    }

    public async Task Rename(Guid account, Guid folder, string name, CancellationToken token = default)
    {
        name = OrganizationStore.FolderName(name);
        var identity = await ManagedAccount(account, token);
        if (!IsImap(identity))
        {
            var local = await LocalFolder(account,folder,token);
            if (identity.Provider != "local" || !OrganizationStore.CanRename(local)) throw new InvalidDataException("不能重命名此系统目录。");
            _organization.RenameLocal(local,name); return;
        }
        await OnImap(account, async (client,cache) =>
        {
            var remote = await VerifiedFolder(client, cache, folder, FolderAccess.ReadOnly, token);
            if (Role(remote.Attributes) != "custom" || remote.ParentFolder is null) throw new InvalidDataException("不能重命名此系统目录。");
            var old = remote.FullName;
            await remote.CloseAsync(false, token);
            await remote.RenameAsync(remote.ParentFolder, name, token);
            cache.Renamed(old, remote.FullName, remote.DirectorySeparator, identity.Name);
            return true;
        },token);
    }

    public async Task DeleteEmpty(Guid account, Guid folder, CancellationToken token = default)
    {
        var identity = await ManagedAccount(account, token);
        if (!IsImap(identity))
        {
            var local = await LocalFolder(account,folder,token);
            if (identity.Provider != "local" || !OrganizationStore.CanRename(local)) throw new InvalidDataException("不能删除此系统目录。");
            _organization.DeleteLocal(local); return;
        }
        await OnImap(account, async (client,cache) =>
        {
            var remote = await VerifiedFolder(client, cache, folder, FolderAccess.ReadOnly, token);
            if (Role(remote.Attributes) != "custom" || remote.ParentFolder is null) throw new InvalidDataException("不能删除此系统目录。");
            if (remote.Count != 0 || (await remote.GetSubfoldersAsync(cancellationToken:token)).Count != 0)
                throw new InvalidDataException("只能删除空目录，请先移出邮件和子目录。");
            var path = remote.FullName;
            await remote.CloseAsync(false,token);
            await remote.DeleteAsync(token);
            cache.Detach(path); return true;
        },token);
    }

    private const MessageSummaryItems MarkFields = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags;
    private ManagedMessage Row(Guid folder, IMessageSummary s) => new(s.UniqueId.Id.ToString(), s.Envelope?.Subject ?? "（无主题）",
        s.Flags?.HasFlag(MessageFlags.Seen) == true, s.Flags?.HasFlag(MessageFlags.Flagged) == true, _organization.Labels(folder,s.UniqueId.Id.ToString()));

    public async Task<ManagedPage> Messages(Guid account, Guid folder, int offset, int limit, CancellationToken token = default)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var identity = await ManagedAccount(account, token);
        if (!IsImap(identity))
        {
            await LocalFolder(account, folder, token);
            return _organization.LocalMessages(folder,offset,limit);
        }
        return await OnImap(account, async (client,cache) =>
        {
            var remote = await VerifiedFolder(client, cache, folder, FolderAccess.ReadOnly, token);
            var end = remote.Count - offset - 1;
            if (end < 0) return new ManagedPage([],false);
            var start = Math.Max(0,end-limit+1);
            var rows = await remote.FetchAsync(start,end,MarkFields,token);
            return new ManagedPage(rows.Where(s => s.Index >= start && s.Index <= end).OrderByDescending(s => s.Index).Select(s => Row(folder,s)).ToArray(),start>0);
        },token);
    }

    public async Task<IReadOnlyList<MarkResult>> Mark(Guid account, Guid folder, string[] keys, MailMark mark, string label, CancellationToken token = default)
    {
        if (!Enum.IsDefined(mark)) throw new ArgumentOutOfRangeException(nameof(mark));
        label = label.Trim();
        bool localLabel = mark is MailMark.AddLabel or MailMark.RemoveLabel;
        if (localLabel && (label.Length is 0 or > 100 || label.Any(char.IsControl))) throw new InvalidDataException("请填写 1–100 字的标签。");
        var identity = await ManagedAccount(account, token);
        var results = new List<MarkResult>();
        if (!IsImap(identity))
        {
            await LocalFolder(account, folder, token);
            foreach (var key in keys.Distinct())
            {
                token.ThrowIfCancellationRequested();
                try { results.Add(new(key,true,"本地已保存",_organization.Mark(folder,key,null,mark,label))); }
                catch (InvalidDataException e) { results.Add(new(key,false,e.Message)); }
            }
            return results;
        }
        return await OnImap<IReadOnlyList<MarkResult>>(account, async (client,cache) =>
        {
            var remote = await VerifiedFolder(client, cache, folder, localLabel ? FolderAccess.ReadOnly : FolderAccess.ReadWrite, token);
            var flag = mark is MailMark.Read or MailMark.Unread ? MessageFlags.Seen : MessageFlags.Flagged;
            foreach (var key in keys.Distinct())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!uint.TryParse(key,out var value) || value == 0) throw new InvalidDataException("邮件标识无效。");
                    var uid = new UniqueId(value);
                    if (!localLabel)
                    {
                        if (!remote.PermanentFlags.HasFlag(flag)) throw new InvalidDataException("此服务器目录不支持保存该标记。");
                        if (mark is MailMark.Read or MailMark.Star) await remote.AddFlagsAsync(uid,flag,true,token);
                        else await remote.RemoveFlagsAsync(uid,flag,true,token);
                    }
                    var row = (await remote.FetchAsync(new[] { uid },MarkFields,token)).FirstOrDefault(s => s.UniqueId == uid)
                        ?? throw new InvalidDataException("服务器邮件已移出或删除。");
                    var changed = Row(folder,row);
                    if (!localLabel && (flag == MessageFlags.Seen ? changed.Read : changed.Starred) != (mark is MailMark.Read or MailMark.Star))
                        throw new InvalidDataException("服务器未保存请求的标记。");
                    results.Add(new(key,true,localLabel ? "本地标签已保存" : "服务器标记已确认",_organization.Mark(folder,key,changed,mark,label)));
                }
                catch (InvalidDataException e) { results.Add(new(key,false,e.Message)); }
                catch (Exception e) when (e is IOException or MailKit.CommandException or MailKit.ProtocolException or ServiceNotConnectedException or FolderNotOpenException)
                { results.Add(new(key,false,"服务器未确认，请刷新后核对。")); }
            }
            return results;
        },token);
    }
}
