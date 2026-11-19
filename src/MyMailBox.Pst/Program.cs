using System.Text;
using System.Text.Json;
using XstReader;
using MyMailBox.Pst;

if (args.Length != 3 || args[0] is not ("preview" or "import")) return 1;
try
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    Console.OutputEncoding = new UTF8Encoding(false);
    var file = new XstFile(args[1], args[2] == "" ? null : int.Parse(args[2]));
    Walk(file.ReadFolderTree());
    return 0;

    void Walk(Folder folder)
    {
        var node = folder.Nid.dwValue;
        Write(new("folder", node, folder.ParentFolder?.Nid.dwValue, folder.Name, Count: folder.ContentCount));
        if (args[0] == "import")
        {
            foreach (var item in file.ReadMessages(folder))
            {
                var key = item.Nid.dwValue;
                PstEntry entry;
                try
                {
                    file.ReadMessageDetails(item);
                    var kind = PstMime.Property(item, 0x001a) as string;
                    if (kind == null || !kind.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
                        entry = new("skip", key, node, item.Subject, Detail: "项目类型：" + (kind ?? "未知"));
                    else if (item.IsEncryptedOrSigned || kind.Contains("SMIME", StringComparison.OrdinalIgnoreCase))
                        entry = new("skip", key, node, item.Subject, Detail: "受保护邮件，请在原客户端读取。");
                    else
                        entry = new("message", key, node, item.Subject, Raw: PstMime.Encode(file, item), IsRead: ((int)item.Flags & 1) != 0,
                            Received: item.Received, Submitted: item.Submitted);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    entry = new("error", key, node, item.Subject, Detail: e.Message);
                }
                finally { item.ClearContents(); item.Properties.Clear(); }
                Write(entry);
            }
            folder.Messages.Clear();
        }
        foreach (var child in folder.Folders) Walk(child);
    }
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

static void Write(PstEntry entry) => Console.WriteLine(JsonSerializer.Serialize(entry));

namespace MyMailBox.Pst
{
    public sealed record PstEntry(string Kind, uint Node, uint? Parent, string Name, uint Count = 0,
        byte[] Raw = null, bool IsRead = false, DateTime? Received = null, DateTime? Submitted = null, string Detail = null);
}
