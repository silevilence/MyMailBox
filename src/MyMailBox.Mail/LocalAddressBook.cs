using Microsoft.Data.Sqlite;
using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailDraftStore
{
    private static void AddressSchema(SqliteCommand cmd)
    {
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS address_book(account TEXT NOT NULL,address TEXT COLLATE NOCASE NOT NULL,name TEXT NOT NULL,contact INTEGER NOT NULL,used INTEGER NOT NULL,
                PRIMARY KEY(account,address));
            """;cmd.ExecuteNonQuery();
    }
    public RecipientFields Normalize(RecipientFields fields)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int duplicates=0;
        string Parse(string text,string field)
        {
            if(string.IsNullOrWhiteSpace(text))return "";
            if(text.Contains('\r')||text.Contains('\n')||!InternetAddressList.TryParse(text,out var parsed)||parsed.Count==0||parsed.Any(x=>x is not MailboxAddress)||
                parsed.Mailboxes.Any(m=>!m.Address.Contains('@')))throw new InvalidDataException(field+" 收件地址无效，请修正后再继续。");
            var result=new InternetAddressList();
            foreach(var m in parsed.Mailboxes){if(seen.Add(m.Address))result.Add(m);else duplicates++;}
            return result.ToString();
        }
        return new(Parse(fields.To,"To"),Parse(fields.Cc,"Cc"),Parse(fields.Bcc,"Bcc"),duplicates);
    }
    public Task<IReadOnlyList<AddressChoice>> Suggest(Guid account,string text,CancellationToken token=default)=>Task.Run<IReadOnlyList<AddressChoice>>(()=>
    {
        using var db=_store.Open();using var c=db.CreateCommand();
        db.CreateFunction<string,bool>("suggests",s=>s.Contains(text,StringComparison.OrdinalIgnoreCase));
        c.CommandText="SELECT name,address,contact FROM address_book WHERE account=$a AND (suggests(name) OR suggests(address)) ORDER BY contact DESC,used DESC,name LIMIT 20";
        c.Parameters.AddWithValue("$a",account.ToString());using var r=c.ExecuteReader();var rows=new List<AddressChoice>();
        while(r.Read()){token.ThrowIfCancellationRequested();rows.Add(new(r.GetString(0),r.GetString(1),r.GetBoolean(2)));}return rows;
    },token);
    public void Contact(Guid account,AddressChoice contact)
    {
        var validated=Normalize(new(contact.Address,"",""));var addr=InternetAddressList.Parse(validated.To).Mailboxes.Single();
        using var db=_store.Open();using var c=db.CreateCommand();
        c.CommandText="INSERT INTO address_book VALUES($a,$addr,$name,1,unixepoch()) ON CONFLICT(account,address) DO UPDATE SET contact=1,name=excluded.name";
        c.Parameters.AddWithValue("$a",account.ToString());c.Parameters.AddWithValue("$addr",addr.Address);c.Parameters.AddWithValue("$name",contact.Name.Trim());c.ExecuteNonQuery();
    }
    public void Forget(Guid account,string? address)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText=address is null?"DELETE FROM address_book WHERE account=$a AND NOT contact":"DELETE FROM address_book WHERE account=$a AND address=$addr";
        c.Parameters.AddWithValue("$a",account.ToString());c.Parameters.AddWithValue("$addr",(object?)address ?? DBNull.Value);c.ExecuteNonQuery();
    }
    internal void Remember(Guid account,IReadOnlyList<string> accepted,MimeMessage mime)
    {
        var names=mime.To.Mailboxes.Concat(mime.Cc.Mailboxes).ToLookup(m=>m.Address,StringComparer.OrdinalIgnoreCase);
        using var db=_store.Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
        c.CommandText="""
            INSERT INTO address_book VALUES($a,$addr,$name,0,unixepoch()) ON CONFLICT(account,address) DO UPDATE SET used=excluded.used,
                name=CASE WHEN NOT contact AND excluded.name<>'' THEN excluded.name ELSE name END
            """;
        c.Parameters.AddWithValue("$a",account.ToString());var addr=c.Parameters.Add("$addr",SqliteType.Text);var name=c.Parameters.Add("$name",SqliteType.Text);
        foreach(var item in accepted){addr.Value=item;name.Value=names[item].FirstOrDefault()?.Name ?? "";c.ExecuteNonQuery();}tx.Commit();
    }
}
