using System.Security.Cryptography;
using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed record OutlookRulePart(int Index,int Type,string Field,string[] Values,bool Candidate,string Reason);
public sealed record OutlookSourceRule(string Name,int Order,bool SourceEnabled,bool ClientOnly,int Direction,
    OutlookRulePart[] Conditions,OutlookRulePart[] Exceptions,OutlookRulePart[] Actions);
public sealed record OutlookRuleInventory(int Schema,string Profile,string Store,OutlookSourceRule[] Rules);
public sealed record OutlookRuleConversion(ReceiveRule? Rule,string[] Report);

public static class OutlookRuleImport
{
    public static async Task<OutlookRuleInventory> Read(string file,CancellationToken token)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("规则清单超过 8 MiB。");
        try
        {
            var inventory = await JsonSerializer.DeserializeAsync<OutlookRuleInventory>(stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true }, token);
            if (inventory is not { Schema: 1 } || inventory.Rules.Any(r => r == null || string.IsNullOrWhiteSpace(r.Name) ||
                r.Order is < 1 or > 100000 || r.Conditions.Concat(r.Exceptions).Concat(r.Actions).Any(p => p == null || p.Values.Any(v => v == null))) ||
                inventory.Rules.Select(r => r.Order).Distinct().Count() != inventory.Rules.Length)
                throw new InvalidDataException("规则清单版本、规则顺序或字段无效。");
            return inventory;
        }
        catch (JsonException ex) { throw new InvalidDataException("无法读取规则清单，请使用只读采集脚本导出的 JSON。",ex); }
    }

    public static OutlookRuleConversion Convert(OutlookRuleInventory inventory,OutlookSourceRule source,SearchScope scope,SearchScope? destination)
    {
        var report = new List<string>(); var errors = new List<string>();
        if (scope.Account == null) errors.Add("请选择目标账户或其目录。");
        if (source.Direction != 0) errors.Add("仅能转换收件规则。");
        if (source.Conditions.Length == 0) errors.Add("无条件规则请手动核对适用范围。");
        var condition = Expression(source.Conditions," AND ","条件");
        var exception = Expression(source.Exceptions," OR ","例外");
        var actions = new List<RuleAction>(); bool stop = false;
        foreach (var part in source.Actions)
        {
            string label = $"动作 {part.Index}";
            switch (part.Type,part.Field)
            {
                case (19,"mark-read") when part.Candidate:
                    actions.Add(new(RuleActionKind.Read)); report.Add(label+" → 标为已读"); break;
                case (21,"stop") when part.Candidate:
                    stop = true; report.Add(label+" → 命中后停止后续规则"); break;
                case (2,"categories") when part.Candidate && part.Values.Length>0 && part.Values.All(v => !string.IsNullOrWhiteSpace(v)):
                    foreach (var value in part.Values) actions.Add(new(RuleActionKind.Label,value));
                    report.Add(label+" → 标签 "+string.Join("、",part.Values)); break;
                case (1,"move") when part.Values.Length == 3 && part.Values.All(v => !string.IsNullOrWhiteSpace(v)):
                    if (source.Actions.Count(p=>p.Type==1)>1 || destination?.Folder == null || destination.Account != scope.Account)
                        errors.Add(label+"需要单独映射到此账户的一个目标目录：" + part.Values[0]);
                    else { actions.Add(new(RuleActionKind.Move,Destination:destination.Folder)); report.Add(label+" → "+part.Values[0]+" → "+destination.Name); }
                    break;
                default: errors.Add($"{label}（{part.Type} · {part.Field}）需手动迁移。"); break;
            }
        }
        if (actions.Count == 0) errors.Add("没有可独立执行的动作。");
        if (condition.Length != 0)
        {
            try { ConditionTree.Parse(condition); if (exception.Length!=0) ConditionTree.Parse(exception); }
            catch (ConditionException e) { errors.Add(e.Message); }
        }
        report.AddRange(errors);
        if (errors.Count != 0) return new(null,report.ToArray());
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { inventory.Profile,inventory.Store,source.Name,source.Order,scope.Account,scope.Folder }));
        return new(new(new Guid(hash.AsSpan(0,16)),0,source.Name,scope.Account,scope.Folder,condition,exception,actions.ToArray(),
            source.Order,false,stop,false),report.ToArray());

        string Expression(OutlookRulePart[] parts,string join,string kind)
        {
            var groups = new List<string>();
            foreach (var part in parts)
            {
                string[] fields = (part.Type,part.Field) switch {
                    (2,"subject") => ["subject"], (13,"body") => ["body"], (14,"subject-or-body") => ["subject","body"], _ => []
                };
                if (fields.Length == 0 || !part.Candidate || part.Values.Length == 0 || part.Values.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{kind} {part.Index}（{part.Type} · {part.Field}）需手动核对。"); continue;
                }
                var predicates = from field in fields from value in part.Values select field + ":" + Quote(value);
                var group = "(" + string.Join(" OR ", predicates) + ")";
                groups.Add(group); report.Add($"{kind} {part.Index} → {group}");
            }
            return string.Join(join,groups);
        }
    }
    private static string Quote(string value) => "\"" + value.Replace("\\","\\\\").Replace("\"","\\\"")
        .Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t") + "\"";
}
