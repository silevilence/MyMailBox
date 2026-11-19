using System.Text;
using System.Text.Json;
using MyMailBox.Contracts;
using MyMailBox.Mail;

internal static class OutlookRuleChecks
{
    internal static async Task CompareClassic(string evidence, string source)
    {
        var workspace = Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "classic-rules");
        var store = new SqliteMailStore(workspace);
        var mime = new MimeReader();
        var local = new LocalMailboxService(store, new AccountService(store), mime);
        var target = await local.Create(new(null, "经典 Outlook 对照", "local"));
        var importer = new PstImportService(store, local, mime);
        var input = await importer.Preview(source, 936, default);
        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(evidence, "rule-results.json")));
        var expected = result.RootElement;
        MailChecks.Check(expected.GetProperty("available").GetBoolean() && input.Fingerprint == expected.GetProperty("sourceHash").GetString(), "Different source PST or unavailable Outlook rules");
        await importer.Import(input, target.Id, null, default);
        var folder = (await local.Folders(target.Id)).Single(f => f.Name == "嵌套目录");
        var protocols = new ProtocolMailService(store);
        var archive = await protocols.Create(target.Id, null, "规则匹配");
        var rules = new ReceiveRuleService(store, new(store, protocols), protocols);
        var inventory = await OutlookRuleImport.Read(Path.Combine(evidence, "rule-inventory.json"), default);
        var original = inventory.Rules.Single(r => r.Name == expected.GetProperty("rule").GetString());
        var converted = OutlookRuleImport.Convert(inventory, original, new(target.Id, folder.Id, folder.Name), new(target.Id, archive, "规则匹配"));
        var draft = converted.Rule ?? throw new Exception(string.Join("\n", converted.Report));
        var preview = await rules.Preview(draft);
        var matches = expected.GetProperty("matched").EnumerateArray().Select(x => x.GetString()).Order().ToArray();
        var actual = preview.Samples.Where(s => s.StartsWith("命中 · ")).Select(s => s["命中 · ".Length..]).Order().ToArray();
        MailChecks.Check(!draft.Enabled && draft.Stop && matches.SequenceEqual(actual) && matches.Length == preview.Matches &&
            preview.Samples.Count == expected.GetProperty("subjects").GetArrayLength(), "Actual Outlook decisions differ from the imported draft");
        Console.WriteLine($"PASS classic Outlook rule comparison: {preview.Samples.Count} original PST messages, {preview.Matches} identical matches, {preview.Exceptions} exceptions; move mapping and stop preserved");
    }

    public static async Task Run()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!,"rule-comparison");
        var store = new SqliteMailStore(path);
        var protocols = new ProtocolMailService(store);
        var rules = new ReceiveRuleService(store,new MailSearchService(store,protocols),protocols);
        var target = await protocols.Create(SqliteMailStore.LocalAccount,null,"归档",default);
        var scope = new SearchScope(SqliteMailStore.LocalAccount,SqliteMailStore.Inbox,"收件箱");
        var destination = scope with { Folder = target,Name = "归档" };
        var source = new OutlookSourceRule("合同",7,true,true,0,
            [new(1,2,"subject",["Order","Invoice"],true,"")],
            [new(1,2,"subject",["private","skip"],true,"")],
            [new(1,19,"mark-read",[],true,""), new(2,2,"categories",["审阅"],true,""),
             new(3,1,"move",["Archive","entry-fixture","store-fixture"],false,"Requires mapping."),new(4,21,"stop",[],true,"")]);
        var file = Path.Combine(path,"inventory.json");
        await File.WriteAllBytesAsync(file,JsonSerializer.SerializeToUtf8Bytes(new OutlookRuleInventory(1,"测试","邮件",[source]),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var inventory = await OutlookRuleImport.Read(file,default);
        var mapped = OutlookRuleImport.Convert(inventory,inventory.Rules.Single(),scope,destination);
        var draft = mapped.Rule ?? throw new Exception(string.Join("/",mapped.Report));
        MailChecks.Check(!draft.Enabled && draft.Stop && draft.Order==7 && draft.Account==scope.Account && draft.Folder==scope.Folder &&
            draft.Actions.Select(a=>a.Kind).SequenceEqual([RuleActionKind.Read,RuleActionKind.Label,RuleActionKind.Move]) && draft.Actions.Last().Destination==target,"Converted rule changed action semantics");
        var subjects = new[]{"Invoice summer","Order new","Order private","Invoice skip","Unrelated"};
        foreach (var subject in subjects)
        {
            var raw = Encoding.UTF8.GetBytes("From: a@example.invalid\r\nTo: b@example.invalid\r\nSubject: "+subject+"\r\n\r\nbody");
            await store.Import(SqliteMailStore.Inbox,raw,await new MimeReader().Parse(raw));
        }
        var preview = await rules.Preview(draft);
        foreach (var subject in subjects)
        {
            var include = source.Conditions[0].Values.Any(word=>subject.Contains(word,StringComparison.OrdinalIgnoreCase));
            var except = source.Exceptions[0].Values.Any(word=>subject.Contains(word,StringComparison.OrdinalIgnoreCase));
            var expected = !include ? "未命中" : except ? "被例外排除" : "命中";
            MailChecks.Check(preview.Samples.Contains(expected+" · "+subject),"Source and converted rule disagreed on "+subject);
        }
        await rules.Save(draft);
        MailChecks.Check(!(await rules.Dashboard()).Rules.Single().Enabled && await rules.Process()==0,"Preview or saving executed imported rule");
        foreach (var blocked in new[] {
            source with { Direction=1 }, source with { Exceptions=[new(1,999,"opaque",[],false,"")] },
            source with { Actions=[..source.Actions,new(5,20,"script",[],false,"")] }, source with { Conditions=[] } })
            MailChecks.Check(OutlookRuleImport.Convert(inventory,blocked,scope,destination).Rule==null,"Unsupported rule became executable");
        MailChecks.Check(OutlookRuleImport.Convert(inventory,source,scope,null).Rule==null,"Move destination inferred");
        MailChecks.Check(OutlookRuleImport.Convert(inventory,source,scope,destination with { Account=Guid.NewGuid() }).Rule==null,"Cross-account move mapping accepted");
        await File.WriteAllTextAsync(file,"{}");
        await PluginChecks.Reject(()=>OutlookRuleImport.Read(file,default),"规则清单");
        Console.WriteLine("PASS Outlook rule inventory: same MIME corpus, explicit account/destination, order, exceptions, stop, disabled draft, unsupported actions, malformed input");
    }
}
