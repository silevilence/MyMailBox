using MyMailBox.Contracts;
using MyMailBox.Mail;
using MyMailBox.Runtime;
using System.Diagnostics;

internal static partial class ComposerChecks
{
    public static void Crash(string directory,Guid account)
    {
        var drafts=new MailDraftStore(new SqliteMailStore(directory));
        drafts.Save(MailDraft.New(account,"first@example.test") with{To="second@example.test",Subject="恢复草稿",Body=new("合同","<b>合同</b>","合同",true)});
        Environment.Exit(77);
    }
    public static async Task Drafts()
    {
        var directory=Path.Combine(PluginChecks.Repository,"artifacts/checks/drafts-"+Guid.NewGuid().ToString("N"));
        var account=Guid.NewGuid();var other=Guid.NewGuid();var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{typeof(ComposerChecks).Assembly.Location,"draft-crash-worker",directory,account.ToString()})start.ArgumentList.Add(arg);
        using(var child=Process.Start(start)!){await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));MailChecks.Check(child.ExitCode==77,"Draft process did not exit at save");}
        var drafts=new MailDraftStore(new SqliteMailStore(directory));var d=drafts.List(account).Single();MailChecks.Check(d.Subject=="恢复草稿"&&d.Body.Html.Contains("<b>")&&drafts.List(other).Count==0,"Restart mixed account identity or body");
        var pages=new Contributions();await using var runtime=new PluginRuntime(pages);var example=PluginChecks.Example();
        await runtime.Start(example with{Manifest=example.Manifest with{Entry="plain-editor",Permissions=["mail.compose"]}},new(){InstanceId="alternate",Grants=["mail.compose"]});
        var editor=pages.Editors.Single();var plain=editor.Open(d.Body);
        var edited=drafts.Save(d with{Body=plain with{Plain="teh edited"}});MailChecks.Check(!edited.Body.Rich&&edited.Body.Html==d.Body.Html&&
            (await editor.Spell(edited.Body.Plain,default)).Contains("the")&&editor.Templates.Count==1,"Alternative editor lost formatting, template or spelling");
        await PluginChecks.Reject(()=>Task.Run(()=>drafts.Save(d with{Subject="stale"})),"尚未保存");
        bool closed=false;pages.BeforePluginStop+=instance=>{if(instance=="alternate"){edited=drafts.Save(edited with{Subject="退出前保存"});closed=true;}return Task.CompletedTask;};
        await runtime.Stop("alternate");MailChecks.Check(closed&&pages.Editors.Count==0&&drafts.List(account).Single().Subject=="退出前保存","Plugin stop lost draft");
        await PluginChecks.Reject(()=>Task.Run(()=>editor.Open(plain)),"停止接单");
        MailChecks.Check(MailDraftStore.SafeMarkup("<p title='metadata'><em>保留</em></p>")=="<p><em>保留</em></p>","Editor draft formatting not normalized");
        Console.WriteLine("PASS abrupt draft restart, isolated account identity, optimistic conflict, external editor takeover/spell/template, flush before unload and rich fallback");
    }
}
