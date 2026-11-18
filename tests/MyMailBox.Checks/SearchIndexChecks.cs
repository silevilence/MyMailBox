using MyMailBox.Contracts;
using MyMailBox.Mail;
using System.Security.Cryptography;
using System.Text;

internal static partial class SearchChecks
{
    private sealed class InlineProgress(Action<SearchIndexProgress> report) : IProgress<SearchIndexProgress>
    {
        public void Report(SearchIndexProgress value) => report(value);
    }
    public static async Task Index()
    {
        var f = await Fixture();
        Task<SearchPage> Find(string value) => f.Search.Search(new([new("body", SearchMatch.Contains, value)]));
        foreach (string term in new[] { "采", "采购", "合同", "购合同。第" })
        {
            var page = await Find(term);
            MailChecks.Check(page.Items.Single().Excerpt?.Match == term && page.IndexedBodies == 2, "Missing exact highlight " + term);
        }
        var raw = Encoding.UTF8.GetBytes("From: a@example.test\r\nSubject: updated\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>报价 AÉñ 😊 発注</p><script>discardme</script>");
        using var db = f.Store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE messages SET raw=$raw,sha256=$sha WHERE id=$id";
        cmd.Parameters.AddWithValue("$raw", raw); cmd.Parameters.AddWithValue("$sha", Convert.ToHexString(SHA256.HashData(raw)));
        cmd.Parameters.AddWithValue("$id", f.Hit.ToString()); cmd.ExecuteNonQuery();
        MailChecks.Check((await Find("采购")).Items.Count == 0 && (await Find("aéÑ 😊 発")).Items.Single().Excerpt?.Match == "AÉñ 😊 発" &&
            (await Find("discardme")).Items.Count == 0, "Changed HTML or multilingual text not indexed");
        cmd.CommandText = "UPDATE body_index_state SET version=0"; cmd.ExecuteNonQuery();
        MailChecks.Check((await Find("発")).Items.Single().LocalId == f.Hit, "Index version repair failed");
        bool browsed = false;
        await f.Search.RebuildBodyIndex(new InlineProgress(p =>
        {
            if (p.Total > p.Completed && !browsed) browsed = f.Store.List(SqliteMailStore.Inbox, 0, 10).GetAwaiter().GetResult().Items.Count == 2;
        }));
        MailChecks.Check(browsed, "Mailbox listing blocked during rebuild");
        cmd.CommandText = "DELETE FROM messages WHERE id=$id"; cmd.ExecuteNonQuery();
        MailChecks.Check((await Find("発")).Items.Count == 0, "Deleted message remained");
        cmd.CommandText = "SELECT count(*) FROM body_fts"; MailChecks.Check((long)cmd.ExecuteScalar()! == 1, "Deleted postings remained");
        Console.WriteLine("PASS FTS5 single rune/bigram indexing, exact multilingual snippets, HTML extraction, update/delete/version repair and concurrent browsing");
    }
}
