namespace MyMailBox.Contracts;

public sealed record SearchScope(Guid? Account, Guid? Folder, string Name);
public sealed record SavedSearch(Guid Id, string Name, string Expression, SearchOrder Order, Guid? Account, Guid? Folder)
{
    public ConditionTree Condition()
    {
        var parts = new List<ConditionNode>();
        if (!string.IsNullOrWhiteSpace(Expression)) parts.Add(ConditionTree.Parse(Expression).Root);
        if (Account.HasValue) parts.Add(ConditionEditor.Leaf(new("accountid", SearchMatch.Equals, Account.Value.ToString())));
        if (Folder.HasValue) parts.Add(ConditionEditor.Leaf(new("folderid", SearchMatch.Equals, Folder.Value.ToString())));
        return new(ConditionTree.CurrentVersion, parts.Count switch
        {
            0 => ConditionEditor.Leaf(new("size", SearchMatch.AtLeast, "0")),
            1 => parts[0],
            _ => new(ConditionLogic.And, null, parts.ToArray())
        });
    }

    public bool HasScope(SearchLibrary library) => library.Scopes.Any(s => s.Account == Account && s.Folder == Folder);
}

public sealed record SearchLibrary(IReadOnlyList<SearchScope> Scopes, IReadOnlyList<SavedSearch> Saved);

public static class ConditionEditor
{
    public static ConditionNode Leaf(FieldCriterion criterion) => new(ConditionLogic.Predicate, criterion, []);
    public static string Expression(ConditionNode node)
    {
        if (node.Criterion is { } c)
        {
            var op = c.Match switch { SearchMatch.Contains => ":", SearchMatch.Equals => "=", SearchMatch.StartsWith => "^=",
                SearchMatch.EndsWith => "$=", SearchMatch.Regex => "~", SearchMatch.Exists => "?", SearchMatch.Before => "<",
                SearchMatch.OnOrAfter or SearchMatch.AtLeast => ">=", SearchMatch.AtMost => "<=", _ => throw new InvalidDataException("无法转换匹配方式。") };
            return c.Field + op + (c.Match == SearchMatch.Exists ? "" : "\"" + c.Value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n","\\n").Replace("\r","\\r").Replace("\t","\\t") + "\"");
        }
        if (node.Logic == ConditionLogic.Not) return "NOT (" + Expression(node.Children.Single()) + ")";
        return "(" + string.Join(node.Logic == ConditionLogic.And ? " AND " : " OR ", node.Children.Select(Expression)) + ")";
    }

    public static ConditionNode? Replace(ConditionNode root, ConditionNode target, ConditionNode? replacement)
    {
        if (ReferenceEquals(root, target)) return replacement;
        if (root.Criterion is not null) return root;
        var children = root.Children.Select(c => Replace(c, target, replacement)).OfType<ConditionNode>().ToArray();
        if (children.Length == 0) return null;
        if (children.Length == 1 && root.Logic != ConditionLogic.Not) return children[0];
        return root with { Children = children };
    }

    public static string Describe(ConditionNode node, IReadOnlyList<SearchField> fields)
    {
        if (node.Criterion is { } c)
        {
            var name = fields.FirstOrDefault(f => f.Id == c.Field)?.Name ?? c.Field;
            var op = c.Match switch { SearchMatch.Contains => "包含", SearchMatch.Equals => "等于", SearchMatch.StartsWith => "开头为",
                SearchMatch.EndsWith => "结尾为", SearchMatch.Regex => "匹配正则", SearchMatch.Exists => "有值", SearchMatch.Before => "早于",
                SearchMatch.AtLeast or SearchMatch.OnOrAfter => "不少于", SearchMatch.AtMost => "不超过", _ => c.Match.ToString() };
            return $"{name}{op}{c.Value}";
        }
        if (node.Logic == ConditionLogic.Not) return "排除（" + Describe(node.Children.Single(), fields) + "）";
        return "（" + string.Join(node.Logic == ConditionLogic.And ? " 并且 " : " 或者 ", node.Children.Select(n => Describe(n,fields))) + "）";
    }
}
