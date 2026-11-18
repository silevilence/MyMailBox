using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;
using System.Text.RegularExpressions;

namespace MyMailBox.Mail;

public sealed partial class MailSearchService
{
    internal void ValidateCondition(ConditionTree tree)
    {
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        Compile(db,cmd,tree,[],default,validateOnly:true);
    }

    public Task<ConditionResult> Evaluate(string messageId, ConditionTree condition, CancellationToken token = default) => Task.Run(() =>
    {
        _metadata.Prepare(token); _bodyIndex.Prepare(false, null, token);
        using var db = _store.Open(); using var cmd = db.CreateCommand();
        var expression = Compile(db, cmd, condition, [], token, singleId:messageId);
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.CommandText = Documents + $" SELECT {expression} FROM docs WHERE id=$id";
        return cmd.ExecuteScalar() switch { long n => n == 0 ? ConditionResult.False : ConditionResult.True, _ => ConditionResult.Unknown };
    }, token);

    private string Compile(SqliteConnection db, SqliteCommand cmd, ConditionTree tree, HashSet<string> unavailable, CancellationToken token, bool validateOnly=false, string? singleId=null, List<string>? highlights=null,bool preview=false,int seed=0)
    {
        if (tree.Version != ConditionTree.CurrentVersion) throw new InvalidDataException("无法识别条件树版本。");
        int count = seed;
        string Visit(ConditionNode node, int depth)
        {
            if (++count - seed > 512 || depth > 40) throw new ConditionException(node.Position, "条件过于复杂。");
            if (node.Logic == ConditionLogic.Predicate && node.Criterion is { } criterion && node.Children.Length == 0)
            {
                if (criterion.Field.StartsWith("plugin.",StringComparison.Ordinal))
                {
                    var extension = _extensions().SingleOrDefault(e => e.Id == criterion.Field && e.Operator == criterion.Operator && criterion.Match == SearchMatch.Extension);
                    if (extension is null) { unavailable.Add(criterion.Field); return "NULL"; }
                    return validateOnly ? "NULL" : PrepareExtension(db, criterion, extension, count, token, singleId, highlights,preview);
                }
                var field = BuiltinFields.SingleOrDefault(f => f.Id == criterion.Field);
                if (field is null)
                {
                    if (!criterion.Field.StartsWith("plugin.", StringComparison.Ordinal)) throw new ConditionException(node.Position, "未知字段 " + criterion.Field);
                    unavailable.Add(criterion.Field); return "NULL";
                }
                var column = "\"" + field.Id + "\"";
                if (criterion.Match == SearchMatch.Exists) return $"CASE WHEN {column} IS NOT NULL AND {column} <> '' THEN 1 END";
                if (field.Kind != SearchKind.Text)
                {
                    if (field.Kind == SearchKind.LocalDate && criterion.Match == SearchMatch.AtLeast) criterion = criterion with { Match = SearchMatch.OnOrAfter };
                    try { return Filter(cmd, criterion, 1000 + count); }
                    catch (InvalidDataException e) { throw new ConditionException(node.Position, e.Message); }
                }
                var mode = criterion.Match;
                Regex? regex = null;
                if (mode == SearchMatch.Regex)
                {
                    try { regex = new Regex(criterion.Value, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException) { throw new ConditionException(node.Position, "不支持的正则表达式：" + e.Message); }
                }
                else if (mode is not (SearchMatch.Equals or SearchMatch.Contains or SearchMatch.StartsWith or SearchMatch.EndsWith))
                    throw new ConditionException(node.Position, "文本字段不支持此匹配方式。");
                var fn = "mb_test_" + count;
                var param = "$v" + count; cmd.Parameters.AddWithValue(param, criterion.Value);
                db.CreateFunction<string?,string,long?>(fn, (s, v) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(s)) return null;
                    const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
                    try
                    {
                        bool match = mode switch { SearchMatch.Equals => s.Equals(v, comparison), SearchMatch.Contains => s.Contains(v, comparison),
                            SearchMatch.StartsWith => s.StartsWith(v, comparison), SearchMatch.EndsWith => s.EndsWith(v, comparison), _ => regex!.IsMatch(s) };
                        token.ThrowIfCancellationRequested(); return match ? 1L : 0L;
                    }
                    catch (RegexMatchTimeoutException) { return null; }
                });
                if (!preview && field.Id == "body" && mode == SearchMatch.Contains)
                    return BodySearchIndex.Contains(cmd, $"{fn}({column},{param})", criterion.Value, param + "grams");
                return field.Id == "tag" ? $"CASE WHEN tag IS NOT NULL THEN EXISTS(SELECT 1 FROM json_each(tag) WHERE {fn}(value,{param})) END" : $"{fn}({column},{param})";
            }
            if (node.Logic == ConditionLogic.Not && node.Children.Length == 1) return "(NOT " + Visit(node.Children[0], depth + 1) + ")";
            if (node.Logic is ConditionLogic.And or ConditionLogic.Or && node.Children.Length >= 2)
                return "(" + string.Join(node.Logic == ConditionLogic.And ? " AND " : " OR ", node.Children.Select(n => Visit(n, depth + 1))) + ")";
            throw new ConditionException(node.Position, "条件树结构无效。");
        }
        var sql = Visit(tree.Root, 0);
        // A missing provider pauses the expression, even inside an otherwise successful OR.
        return unavailable.Count > 0 ? "NULL" : sql;
    }
}
