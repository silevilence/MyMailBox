using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;

internal static partial class SearchChecks
{
    public static async Task Extension()
    {
        var f = await Fixture(); var contributions = new Contributions();
        await using var runtime = new PluginRuntime(contributions);
        var external = PluginChecks.Example();
        external = external with { Manifest = external.Manifest with { Entry="search-case", Permissions=["mail.read"] } };
        var options = new PluginOptions { InstanceId="case-extension", Grants=["mail.read:" + SqliteMailStore.LocalAccount] };
        await runtime.Start(external,options);
        var service = new MailSearchService(f.Store,f.Protocols,()=>contributions.SearchExtensions);
        var field = service.Fields.Single(x=>x.Id=="plugin.example.case.v1");
        using var db = f.Store.Open(); using var c = db.CreateCommand();
        c.CommandText = "UPDATE messages SET subject='采购 [CASE-0042]' WHERE id=$id"; c.Parameters.AddWithValue("$id",f.Hit.ToString()); c.ExecuteNonQuery();
        var saved = new SavedSearch(Guid.NewGuid(),"工单","plugin.example.case.v1@number=42",SearchOrder.Newest,null,null);
        await service.Save(saved);
        var page = await service.Search(new([]){Condition=saved.Condition()});
        MailChecks.Check(field.Operator=="number" && page.Items.Single().LocalId==f.Hit && page.Items[0].Excerpt?.Match=="42", "External operator/derived highlight missing");
        c.CommandText = "SELECT count(*) FROM derived_search_value";
        MailChecks.Check((long)c.ExecuteScalar()! == 1, "Unrelated subject reached expensive extraction");
        MailChecks.Check(await service.EvaluateSaved(f.Hit.ToString(),saved.Id)==ConditionResult.True, "Indexed extension diverged in automation");
        var rules=new ReceiveRuleService(f.Store,service,f.Protocols);
        var preview=await rules.Preview(new(Guid.NewGuid(),0,"扩展预览",null,null,saved.Expression,"",[new(RuleActionKind.Star)],1,false,false,false));
        MailChecks.Check(preview.Matches==1,"Read-only external planner changed semantics");
        await runtime.Stop(options.InstanceId);
        page = await service.Search(new([]){Condition=saved.Condition()});
        MailChecks.Check(page.UnavailableFields.Single()==field.Id && page.Items.Count==0 && (await service.Library()).Saved.Single()==saved &&
            await service.EvaluateSaved(f.Hit.ToString(),saved.Id)==ConditionResult.Unknown, "Plugin unload lost expression or executed rule");
        await runtime.Start(external,new() { InstanceId=options.InstanceId,Grants=[] });
        MailChecks.Check(await service.EvaluateSaved(f.Hit.ToString(),saved.Id)==ConditionResult.Unknown,"Stale cached field bypassed account read grant");
        await runtime.Stop(options.InstanceId);
        Console.WriteLine("PASS external collectible search provider, namespaced operator, candidate prefilter, indexed extraction/highlight, unload/reload and scoped grants");
    }
}
