using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailSearchService
{
    private static IEnumerable<string> HighlightTerms(SearchRequest request)
    {
        static IEnumerable<FieldCriterion> Leaves(ConditionNode node)
        {
            if (node.Criterion is { } leaf) yield return leaf;
            if (node.Logic != ConditionLogic.Not)
                foreach (var child in node.Children)
                    foreach (var criterion in Leaves(child)) yield return criterion;
        }
        return request.Criteria.Concat(request.Condition is null ? [] : Leaves(request.Condition.Root))
            .Where(f => f.Field == "body" && f.Match is SearchMatch.Contains or SearchMatch.Equals or SearchMatch.StartsWith or SearchMatch.EndsWith)
            .Select(f => f.Value).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static SearchExcerpt? Excerpt(string body, string[] terms)
    {
        foreach (var term in terms)
        {
            int start = body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            int left = Math.Max(0, start - 32), end = start + term.Length, right = Math.Min(body.Length, end + 48);
            if (left > 0 && char.IsLowSurrogate(body[left])) left--;
            if (right < body.Length && char.IsLowSurrogate(body[right])) right++;
            return new((left == 0 ? "" : "…") + body[left..start], body[start..end], body[end..right] + (right == body.Length ? "" : "…"));
        }
        return null;
    }
}
