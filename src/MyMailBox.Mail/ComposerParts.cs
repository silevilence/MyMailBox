using MimeKit;
using MyMailBox.Contracts;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyMailBox.Mail;

public sealed partial class MailDraftStore
{
    private void InitializeParts()
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="""
            CREATE TABLE IF NOT EXISTS compose_profile(account TEXT PRIMARY KEY,profile TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS compose_part(id TEXT PRIMARY KEY,draft TEXT NOT NULL,account TEXT NOT NULL,hash TEXT NOT NULL,
                inline INTEGER NOT NULL,metadata TEXT NOT NULL,data BLOB NOT NULL,UNIQUE(draft,hash,inline));
            """;c.ExecuteNonQuery();
    }
    public SendingProfile Profile(Guid account)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="SELECT profile FROM compose_profile WHERE account=$a";c.Parameters.AddWithValue("$a",account.ToString());
        return c.ExecuteScalar() is string json?JsonSerializer.Deserialize<SendingProfile>(json)!:new("",[]);
    }
    public void Configure(Guid account,SendingProfile profile)
    {
        if(profile.MaxBytes is <=0 or >MimeReader.MaxMessageBytes)throw new InvalidDataException("发送上限须在 1 字节至 25 MiB 之间。");
        if(profile.Aliases.Any(s=>!MailboxAddress.TryParse(s,out var address)||!string.Equals(address.Address,s,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("备用发件身份需要有效邮箱地址。");
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="INSERT OR REPLACE INTO compose_profile VALUES($a,$json)";
        c.Parameters.AddWithValue("$a",account.ToString());c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(profile));c.ExecuteNonQuery();
    }
    public async Task<ComposerPart> AddPart(Guid draft,Guid account,string name,Stream data,bool inline,CancellationToken token=default)
    {
        name=Path.GetFileName(name);
        if(string.IsNullOrWhiteSpace(name)||name.Any(char.IsControl))throw new InvalidDataException("文件名无效。");
        long limit=Profile(account).MaxBytes;
        if(data.CanSeek && data.Length-data.Position>limit)throw new InvalidDataException("附件超过当前账户上限。");
        using var buffer=new MemoryStream();byte[] block=new byte[81920];int count;
        while((count=await data.ReadAsync(block,token))>0)
        {if(buffer.Length+count>limit)throw new InvalidDataException("附件超过当前账户上限。");await buffer.WriteAsync(block.AsMemory(0,count),token);}
        token.ThrowIfCancellationRequested();var bytes=buffer.ToArray();var hash=Convert.ToHexString(SHA256.HashData(bytes));var id=Guid.NewGuid();
        var mime=inline?ImageType(bytes):MimeTypes.GetMimeType(name);
        var part=new ComposerPart(id,name,mime,bytes.Length,inline?id.ToString("N")+"@mymailbox.invalid":null);
        using var db=_store.Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
        c.CommandText="INSERT OR IGNORE INTO compose_part VALUES($id,$d,$a,$hash,$inline,$meta,$data)";
        c.Parameters.AddWithValue("$id",id.ToString());c.Parameters.AddWithValue("$d",draft.ToString());c.Parameters.AddWithValue("$a",account.ToString());
        c.Parameters.AddWithValue("$hash",hash);c.Parameters.AddWithValue("$inline",inline);c.Parameters.AddWithValue("$meta",JsonSerializer.Serialize(part));c.Parameters.AddWithValue("$data",bytes);c.ExecuteNonQuery();
        c.CommandText="SELECT metadata FROM compose_part WHERE draft=$d AND hash=$hash AND inline=$inline AND account=$a";
        if(c.ExecuteScalar() is not string json)throw new InvalidDataException("附件所属草稿账户不匹配。");
        token.ThrowIfCancellationRequested();tx.Commit();return JsonSerializer.Deserialize<ComposerPart>(json)!;
    }
    private static string ImageType(byte[] value)
    {
        var b=value.AsSpan();
        if(b.StartsWith(new byte[]{137,80,78,71,13,10,26,10}))return "image/png";
        if(b.StartsWith(new byte[]{255,216,255}))return "image/jpeg";
        if(b.StartsWith("GIF87a"u8)||b.StartsWith("GIF89a"u8))return "image/gif";
        if(b.Length>=12&&b[..4].SequenceEqual("RIFF"u8)&&b.Slice(8,4).SequenceEqual("WEBP"u8))return "image/webp";
        throw new InvalidDataException("内联图片支持 PNG、JPEG、GIF 和 WebP。");
    }
    private (ComposerPart Info,byte[] Data) Part(Guid account,Guid id)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="SELECT metadata,data FROM compose_part WHERE id=$id AND account=$a";
        c.Parameters.AddWithValue("$id",id.ToString());c.Parameters.AddWithValue("$a",account.ToString());using var r=c.ExecuteReader();
        if(!r.Read())throw new InvalidDataException("附件缺失或不属于此账户，请移除并重新添加。");
        return(JsonSerializer.Deserialize<ComposerPart>(r.GetString(0))!,(byte[])r[1]);
    }
    public byte[] PartData(Guid account,Guid id)=>Part(account,id).Data;

    internal MimeEntity Body(Guid account,TextMail mail,CancellationToken token)
    {
        var builder=new BodyBuilder{TextBody=mail.Body,HtmlBody=mail.Html is null?null:SafeMarkup(mail.Html)};
        var profile=Profile(account);long total=0;
        foreach(var id in mail.PartIds.Distinct())
        {
            token.ThrowIfCancellationRequested();var (part,data)=Part(account,id);total+=data.LongLength;
            if(total>profile.MaxBytes)throw new InvalidDataException("附件总量超过当前账户上限。");
            var collection=part.Cid is null || mail.Html is null ? builder.Attachments : builder.LinkedResources;
            var entity=collection.Add(part.Name,data,ContentType.Parse(part.Mime));
            if(part.Cid is not null)entity.ContentId=part.Cid;
        }
        return builder.ToMessageBody();
    }
}
