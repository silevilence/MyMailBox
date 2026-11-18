using AngleSharp.Html.Parser;
using MyMailBox.Contracts;
using System.Text.Json;

namespace MyMailBox.Mail;

public sealed partial class MailDraftStore : IMailDrafts
{
    private readonly SqliteMailStore _store;
    public MailDraftStore(SqliteMailStore store)
    {
        _store=store;using var db=store.Open();using var c=db.CreateCommand();
        c.CommandText="CREATE TABLE IF NOT EXISTS compose_draft(id TEXT PRIMARY KEY,version INTEGER NOT NULL,account TEXT NOT NULL,content TEXT NOT NULL,submitted INTEGER NOT NULL DEFAULT 0,changed INTEGER NOT NULL)";c.ExecuteNonQuery();InitializeParts();AddressSchema(c);
    }
    public IReadOnlyList<MailDraft> List(Guid account)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="SELECT content FROM compose_draft WHERE account=$a AND NOT submitted ORDER BY changed DESC,rowid DESC";
        c.Parameters.AddWithValue("$a",account.ToString());using var r=c.ExecuteReader();var drafts=new List<MailDraft>();
        while(r.Read())drafts.Add(JsonSerializer.Deserialize<MailDraft>(r.GetString(0))!);return drafts;
    }
    public MailDraft Save(MailDraft draft)
    {
        var next=draft with{Version=draft.Version+1};
        using var db=_store.Open();using var c=db.CreateCommand();
        c.CommandText=draft.Version==0 ?
            "INSERT OR IGNORE INTO compose_draft VALUES($id,$next,$a,$json,0,$time)" :
            "UPDATE compose_draft SET version=$next,content=$json,changed=$time WHERE id=$id AND account=$a AND version=$old AND NOT submitted";
        c.Parameters.AddWithValue("$old",draft.Version);c.Parameters.AddWithValue("$next",next.Version);c.Parameters.AddWithValue("$id",draft.Id.ToString());
        c.Parameters.AddWithValue("$a",draft.Account.ToString());c.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(next));
        if(c.ExecuteNonQuery()!=1)throw new InvalidDataException("草稿已在其他编辑器修改或提交，当前编辑尚未保存。");
        return next;
    }
    public void Submitted(Guid draft,int version)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="UPDATE compose_draft SET submitted=1 WHERE id=$id AND version=$v";
        c.Parameters.AddWithValue("$id",draft.ToString());c.Parameters.AddWithValue("$v",version);
        if(c.ExecuteNonQuery()!=1)throw new InvalidDataException("草稿版本已变化，请核对发件箱记录。");
    }

    public static string SafeMarkup(string html)
    {
        // The editor accepts only its own formatting vocabulary. Strip attributes, including event handlers.
        using var doc=new HtmlParser().ParseDocument(html);
        foreach(var e in doc.QuerySelectorAll("script,style,iframe,object,svg,math,form,template"))e.Remove();
        foreach(var e in doc.Body!.QuerySelectorAll("*").Reverse().ToArray())
        {
            if(e.LocalName=="img" && e.GetAttribute("src") is {} src && src.StartsWith("cid:",StringComparison.OrdinalIgnoreCase) &&
                src.Length<128 && src[4..].All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '.' or '@'))
            {foreach(var attr in e.Attributes.Where(a=>a.Name is not ("src" or "alt")).ToArray())e.RemoveAttribute(attr.Name);}
            else if(e.LocalName is "b" or "strong" or "i" or "em" or "u" or "s" or "p" or "br" or "div" or "ol" or "ul" or "li" or "blockquote")
            {foreach(var attr in e.Attributes.ToArray())e.RemoveAttribute(attr.Name);}
            else{var parent=e.Parent!;while(e.FirstChild is {} child)parent.InsertBefore(child,e);e.Remove();}
        }
        return doc.Body.InnerHtml;
    }
}
