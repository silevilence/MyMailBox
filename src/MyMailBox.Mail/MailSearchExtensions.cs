using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyMailBox.Mail;

public sealed partial class MailSearchService
{
    private static string PrepareExtension(SqliteConnection db, FieldCriterion criterion, SearchExtension extension, int index,
        CancellationToken token, string? singleId, List<string>? highlights)
    {
        string input = extension.Input switch { SearchInput.Subject => "subject", SearchInput.From => "\"from\"", SearchInput.Headers => "header",
            SearchInput.Body => "body", _ => throw new InvalidDataException("未知扩展输入。") };
        string table = "temp.ext_result_" + index, allowed = "ext_allowed_" + index, candidate = "ext_candidate_" + index;
        var contains = extension.CandidateContains(criterion.Value);
        db.CreateFunction<string,bool>(allowed, s => extension.CanRead(Guid.Parse(s)));
        db.CreateFunction<string?,bool?>(candidate, s => s is null ? null : string.IsNullOrEmpty(contains) || s.Contains(contains,StringComparison.OrdinalIgnoreCase));
        using var read = db.CreateCommand();
        read.CommandText = $"CREATE TEMP TABLE {table}(id TEXT PRIMARY KEY,matched INTEGER,highlight TEXT)"; read.ExecuteNonQuery();
        read.CommandText = Documents + $"""
             SELECT docs.id,accountid,{input},v.source,v.value FROM docs
             LEFT JOIN derived_search_value v ON v.id=docs.id AND v.field=$field
             WHERE docs.id>$after AND {allowed}(accountid) AND {candidate}({input})
            """ + (singleId is null ? "" : " AND docs.id=$only") + " ORDER BY docs.id LIMIT 32";
        read.Parameters.AddWithValue("$field",extension.Id); var after = read.Parameters.AddWithValue("$after","");
        if (singleId is not null) read.Parameters.AddWithValue("$only",singleId);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var fresh = new List<SearchCandidate>(); var ready = new List<SearchCandidate>();
            var hashes = new Dictionary<string,string>(); var accounts = new Dictionary<string,Guid>();
            using (var r = read.ExecuteReader()) while (r.Read())
            {
                string id = r.GetString(0), text = r.GetString(2); var account = Guid.Parse(r.GetString(1));
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                accounts.Add(id,account); after.Value = id;
                if (!r.IsDBNull(3) && r.GetString(3) == hash)
                {
                    if (!r.IsDBNull(4)) ready.Add(new(id, account,r.GetString(4)));
                }
                else { fresh.Add(new(id,account,text)); hashes.Add(id,hash); }
            }
            if (accounts.Count == 0) break;
            var extracted = fresh.Count == 0 ? [] : extension.Extract(fresh,token).GetAwaiter().GetResult();
            foreach (var item in extracted)
                if (hashes.ContainsKey(item.Id) && item.Value is not null) ready.Add(new(item.Id, accounts[item.Id],item.Value));
            var matched = ready.Count == 0 ? [] : extension.Match(criterion.Value,ready,token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            using var tx = db.BeginTransaction(); using var write = db.CreateCommand(); write.Transaction = tx;
            foreach (var item in extracted)
            {
                if (!hashes.TryGetValue(item.Id,out var hash)) continue;
                write.Parameters.Clear(); write.CommandText = "INSERT OR REPLACE INTO derived_search_value VALUES($field,$id,$hash,$value)";
                write.Parameters.AddWithValue("$field",extension.Id); write.Parameters.AddWithValue("$id",item.Id);
                write.Parameters.AddWithValue("$hash",hash); write.Parameters.AddWithValue("$value",(object?)item.Value ?? DBNull.Value); write.ExecuteNonQuery();
            }
            foreach (var item in matched)
            {
                if (!accounts.ContainsKey(item.Id)) continue;
                write.Parameters.Clear(); write.CommandText = $"INSERT OR REPLACE INTO {table} VALUES($id,$match,$highlight)";
                write.Parameters.AddWithValue("$id",item.Id); write.Parameters.AddWithValue("$match",(object?)item.Match ?? DBNull.Value);
                write.Parameters.AddWithValue("$highlight",item is {Match:true,Highlight:not null} ? JsonSerializer.Serialize(item.Highlight) : (object)DBNull.Value);
                write.ExecuteNonQuery();
            }
            tx.Commit();
        }
        highlights?.Add($"(SELECT e.highlight FROM {table} e WHERE e.id=docs.id)");
        return $"(CASE WHEN NOT {allowed}(accountid) OR {input} IS NULL THEN NULL WHEN NOT {candidate}({input}) THEN 0 ELSE (SELECT e.matched FROM {table} e WHERE e.id=docs.id) END)";
    }
}
