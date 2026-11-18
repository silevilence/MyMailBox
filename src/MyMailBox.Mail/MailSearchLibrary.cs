using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailSearchService
{
    private void InitializeLibrary()
    {
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS saved_search(id TEXT PRIMARY KEY,name TEXT NOT NULL,expression TEXT NOT NULL,
                sort INTEGER NOT NULL,account TEXT,folder TEXT,version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS remote_search_hit(batch TEXT NOT NULL,id TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(batch,id));
            """;
        cmd.ExecuteNonQuery();
    }

    public Task<SearchLibrary> Library(CancellationToken token = default) => Task.Run(() =>
    {
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT a.id,NULL,a.name FROM accounts a WHERE a.enabled AND NOT a.removed
            UNION ALL SELECT a.id,f.id,a.name || ' · ' || f.path FROM folders f JOIN accounts a ON a.id=f.account_id WHERE a.enabled AND NOT a.removed
            ORDER BY 3
            """;
        var scopes = new List<SearchScope> { new(null, null, "全部可用账户") };
        using (var r = cmd.ExecuteReader()) while (r.Read()) scopes.Add(new(Guid.Parse(r.GetString(0)), r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)), r.GetString(2)));
        cmd.CommandText = "SELECT id,name,expression,sort,account,folder,version FROM saved_search ORDER BY name,id";
        var searches = new List<SavedSearch>();
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            if (r.GetInt32(6) != ConditionTree.CurrentVersion) throw new InvalidDataException("已保存的搜索需要更新版本。");
            searches.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), (SearchOrder)r.GetInt32(3),
                r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4)), r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5))));
        }
        return new SearchLibrary(scopes, searches);
    }, token);

    public async Task<ConditionResult> EvaluateSaved(string messageId, Guid savedId, CancellationToken token = default)
    {
        var library = await Library(token);
        var search = library.Saved.FirstOrDefault(s => s.Id == savedId);
        return search is null || !search.HasScope(library) ? ConditionResult.Unknown : await Evaluate(messageId, search.Condition(), token);
    }

    public async Task Save(SavedSearch search, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(search.Name)) throw new InvalidDataException("请填写搜索名称。");
        if (!search.HasScope(await Library(token))) throw new InvalidDataException("搜索范围已失效，请重新选择。");
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        Compile(db, cmd, search.Condition(), [], token);
        cmd.CommandText = """
            INSERT INTO saved_search VALUES($id,$name,$expression,$sort,$account,$folder,$version)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,expression=excluded.expression,sort=excluded.sort,
                account=excluded.account,folder=excluded.folder,version=excluded.version
            """;
        cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", search.Id.ToString()); cmd.Parameters.AddWithValue("$name", search.Name.Trim());
        cmd.Parameters.AddWithValue("$expression", search.Expression); cmd.Parameters.AddWithValue("$sort", (int)search.Order);
        cmd.Parameters.AddWithValue("$account", (object?)search.Account?.ToString() ?? DBNull.Value); cmd.Parameters.AddWithValue("$folder", (object?)search.Folder?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$version", ConditionTree.CurrentVersion); token.ThrowIfCancellationRequested(); cmd.ExecuteNonQuery();
    }
}
