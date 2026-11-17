using CordiSharp;
using MyMailBox.Contracts;
using MyMailBox.Mail;

namespace MyMailBox.Builtins;

public static class ArchivePage
{
    public static void Apply(Context context, SqliteMailStore store)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        var service = new MailArchive(store); var selections = new SelectionService(store, new AccountService(store));
        CancellationTokenSource? operation = null;
        context.Effect(() => new Cleanup(() => operation?.Cancel()));
        context.Effect(() => host.Add(new Page("archive", "邮件归档包", "归档只包含所选邮件、目录与账户说明/标签，不包含工作空间设置和凭据。导入为独立本地邮箱；UUID 或 alias 冲突会重新映射，不覆盖现有内容。",
            [new("archive-scope", "导出范围（可先在“写信与整理”保存账户或目录范围）", "inbox", [new("inbox", "默认本地收件箱"), new("account", "已保存的账户范围"), new("folder", "已保存的目录范围")]), new("archive-file", "邮件归档包路径", FilePicker: true)],
            [new("archive-export", "导出选定范围"), new("archive-import", "校验并导入归档"), new("archive-cancel", "取消当前操作")], async (values, action) =>
            {
                if (action == "archive-cancel") { operation?.Cancel(); return "已请求取消；未提交的导入会回滚。"; }
                if (operation is not null) return "当前归档操作尚未完成，请等待或取消。";
                using var cancellation = new CancellationTokenSource(); operation = cancellation;
                try
                {
                    if (action == "archive-import")
                    {
                        var result = await service.Import(values["archive-file"], cancellation.Token);
                        return $"已导入 {result.Accounts} 个账户、{result.Folders} 个目录、{result.Messages} 封邮件。\n重映射：账户 {result.AccountMapping.Count(p => p.Key != p.Value)}、目录 {result.FolderMapping.Count(p => p.Key != p.Value)}、邮件 {result.MessageMapping.Count(p => p.Key != p.Value)}。\n" + string.Join("\n", result.Notes);
                    }
                    Guid[] folders;
                    if (values["archive-scope"] == "inbox") folders = [SqliteMailStore.Inbox];
                    else
                    {
                        var saved = await selections.Load("organize." + values["archive-scope"], cancellation.Token) ?? throw new InvalidDataException("请先在“写信与整理”保存对应范围。");
                        var resolved = await selections.Resolve(saved, cancellation.Token);
                        if (resolved.Missing.Length > 0 || resolved.Choices.Any(c => !c.Available)) throw new InvalidDataException("保存范围存在失效或不可用项，请重新选择。");
                        folders = values["archive-scope"] == "folder" ? resolved.Choices.Select(c => c.Reference.FolderId!.Value).ToArray()
                            : (await store.Folders(cancellation.Token)).Where(f => resolved.Choices.Any(c => c.Reference.AccountId == f.AccountId)).Select(f => f.Id).ToArray();
                    }
                    return "邮件归档已导出：" + await service.Export(folders, values["archive-file"], cancellation.Token);
                }
                finally { operation = null; }
            })));
    }
}
