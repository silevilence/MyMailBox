namespace MyMailBox.Contracts;

public enum ConditionLogic { Predicate, And, Or, Not }
public enum ConditionResult { Unknown, False, True }

public sealed record ConditionNode(ConditionLogic Logic, FieldCriterion? Criterion, ConditionNode[] Children, int Position = 0);
public sealed record ConditionTree(int Version, ConditionNode Root)
{
    public const int CurrentVersion = 1;
    public static ConditionTree Parse(string expression) => new(CurrentVersion, new ConditionParser(expression).Parse());
}

public sealed class ConditionException(int position, string reason) : FormatException($"位置 {position + 1}：{reason}")
{
    public int Position { get; } = position;
}

internal sealed class ConditionParser(string text)
{
    private int _pos;
    private int _nodes;
    public ConditionNode Parse()
    {
        var node = ParseOr(0);
        Space();
        if (_pos != text.Length) throw Error("条件之间需要 AND 或 OR。");
        return node;
    }

    private ConditionNode ParseOr(int depth)
    {
        var nodes = new List<ConditionNode> { ParseAnd(depth) };
        while (Keyword("OR")) nodes.Add(ParseAnd(depth));
        return nodes.Count == 1 ? nodes[0] : new(ConditionLogic.Or, null, nodes.ToArray(), nodes[0].Position);
    }
    private ConditionNode ParseAnd(int depth)
    {
        var nodes = new List<ConditionNode> { Unary(depth) };
        while (Keyword("AND")) nodes.Add(Unary(depth));
        return nodes.Count == 1 ? nodes[0] : new(ConditionLogic.And, null, nodes.ToArray(), nodes[0].Position);
    }
    private ConditionNode Unary(int depth)
    {
        Space(); int start = _pos;
        if (depth > 32 || ++_nodes > 200) throw Error("条件过于复杂。");
        if (Keyword("NOT")) return new(ConditionLogic.Not, null, [Unary(depth + 1)], start);
        if (Eat("("))
        {
            var group = ParseOr(depth + 1); Space();
            if (!Eat(")")) throw Error("缺少右括号。");
            return group;
        }
        while (_pos < text.Length && Identifier(text[_pos])) _pos++;
        if (_pos == start) throw Error("需要字段名。");
        var field = text[start.._pos].ToLowerInvariant();
        Space();
        string? op = null;
        if (Eat("@"))
        {
            int from = _pos;
            while (_pos < text.Length && Identifier(text[_pos])) _pos++;
            op = text[from.._pos];
            if (op.Length == 0 || !Eat("=")) throw Error("扩展运算符格式为 字段@运算符=值。");
        }
        SearchMatch match = op is not null ? SearchMatch.Extension : Eat("?") ? SearchMatch.Exists : Eat("^=") ? SearchMatch.StartsWith : Eat("$=") ? SearchMatch.EndsWith :
            Eat("~") ? SearchMatch.Regex : Eat(">=") ? SearchMatch.AtLeast : Eat("<=") ? SearchMatch.AtMost :
            Eat("<") ? SearchMatch.Before : Eat("=") ? SearchMatch.Equals : Eat(":") ? SearchMatch.Contains : throw Error("未知匹配符。");
        var value = match == SearchMatch.Exists ? "" : ReadValue();
        return new(ConditionLogic.Predicate, new(field, match, value) { Operator = op }, [], start);
    }

    private string ReadValue()
    {
        Space();
        if (!Eat("\""))
        {
            int start = _pos;
            while (_pos < text.Length && !char.IsWhiteSpace(text[_pos]) && text[_pos] is not '(' and not ')') _pos++;
            if (_pos == start) throw Error("需要条件值。");
            return text[start.._pos];
        }
        var value = new System.Text.StringBuilder();
        while (_pos < text.Length)
        {
            char c = text[_pos++];
            if (c == '"') return value.ToString();
            if (c == '\\')
            {
                if (_pos == text.Length) throw Error("转义未完成。");
                c = text[_pos++] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => throw new ConditionException(_pos - 1, "未知转义。") };
            }
            value.Append(c);
        }
        throw Error("缺少结束引号。");
    }
    private static bool Identifier(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-';
    private void Space() { while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++; }
    private bool Keyword(string word)
    {
        Space();
        if (!text.AsSpan(_pos).StartsWith(word, StringComparison.OrdinalIgnoreCase) ||
            _pos + word.Length < text.Length && Identifier(text[_pos + word.Length])) return false;
        _pos += word.Length; return true;
    }
    private bool Eat(string value)
    {
        if (!text.AsSpan(_pos).StartsWith(value, StringComparison.Ordinal)) return false;
        _pos += value.Length; return true;
    }
    private ConditionException Error(string reason) => new(_pos, reason);
}
