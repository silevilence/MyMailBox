using CordiSharp;
using CordiSharp.Registry;
using MyMailBox.Contracts;

namespace ExamplePlugin;

[Plugin("eml-import")]
[Inject("mailbox.host")]
public sealed class ImportPlugin : IPlugin<PluginOptions>
{
    public void Load(Context context, PluginOptions options)
    {
        var host = context.Get<IPluginHost>("mailbox.host")!;
        var format = new ImportFormat("eml", "单封 EML 示例", "*.eml", 1, [new("folder", "导入目录名称", "EML 邮件")]);
        context.Effect(() => host.AddImportReader(new(format, Read)));
    }

    private static async Task Read(ImportReadRequest request, Func<MailImportItem,CancellationToken,Task> receive, CancellationToken token)
    {
        await receive(new("folder", 1, null, request.Parameters.GetValueOrDefault("folder") ?? "EML 邮件", 1), token);
        if (request.Preview) return;
        var name = Path.GetFileName(request.File);
        if (request.Committed.Contains(2u)) await receive(new("duplicate", 2, 1, name), token);
        else if (new FileInfo(request.File).Length > 25 * 1024 * 1024) await receive(new("error", 2, 1, name, Detail: "EML 超过 25 MiB。"), token);
        else await receive(new("message", 2, 1, name, Raw: await File.ReadAllBytesAsync(request.File, token), Detail: "EML 无邮箱已读标志，按未读导入。"), token);
    }
}
