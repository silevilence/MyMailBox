using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using MyMailBox.Mail;
using MyMailBox.Runtime;
using Application = FlaUI.Core.Application;

internal static class OfficeWorkflowChecks
{
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindow(string? className,string title);
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var home=Path.Combine(output,"mail-"+Guid.NewGuid().ToString("N"));
        var store=new SqliteMailStore(Path.Combine(home,"mail"));
        var accounts=new AccountService(store);
        var mailbox=accounts.Save(new(null,"办公邮箱","pop3","office@example.invalid")).GetAwaiter().GetResult();
        accounts.Save(new(null,"工作邮箱","smtp","sender@example.invalid")).GetAwaiter().GetResult();
        var folder=Guid.NewGuid();
        using(var db=store.Open())
        using(var cmd=db.CreateCommand())
        {
            cmd.CommandText="INSERT INTO folders(id,account_id,name,path,role) VALUES($f,$a,'收件箱','办公邮箱 / 收件箱','pop-inbox')";
            cmd.Parameters.AddWithValue("$f",folder.ToString());cmd.Parameters.AddWithValue("$a",mailbox.Id.ToString());cmd.ExecuteNonQuery();
        }
        var parser=new MimeReader();
        var subjects=new[]{"本周会议安排","请核对报销明细","周五项目进展","下周培训时间"};
        for(int i=0;i<subjects.Length;i++)
        {
            var message=new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress(new[]{"王琪","陈敏","李明","张宁"}[i],"team@example.invalid"));
            message.To.Add(MimeKit.MailboxAddress.Parse("office@example.invalid"));message.Date=DateTimeOffset.Now.AddMinutes(-i*15);
            message.Subject=subjects[i];var body=new MimeKit.BodyBuilder{TextBody="请在周五前确认会议时间。\n\n如需调整，请回复这封邮件。"};
            if(i==0)body.Attachments.Add("会议议程.txt",Encoding.UTF8.GetBytes("会议议程"));message.Body=body.ToMessageBody();
            using var stream=new MemoryStream();message.WriteTo(stream);var raw=stream.ToArray();
            store.Import(folder,raw,parser.Parse(raw).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        }
        var exe=Environment.GetEnvironmentVariable("MYMAILBOX_UI_EXE")??throw new InvalidOperationException("Specify published application");
        var start=new ProcessStartInfo(exe){UseShellExecute=false};start.Environment["MYMAILBOX_HOME"]=home;
        using var automation=new UIA3Automation();using var app=Application.Launch(start);
        Window? window=null;
        try
        {
            window=app.GetMainWindow(automation,TimeSpan.FromSeconds(30))??throw new Exception("No window");
            var ui=new Ui(window);
            ui.ListContains("unified-list","本周会议安排");ui.Gone("nav-prototype/read");
            ui.SelectList("unified-list",0);WaitFor(()=>ui.Find("unified-body").AsTextBox().Text.Contains("周五"),"select cached mail to read");
            NativeWindowEvidence.CaptureClient(window, Path.Combine(output,"inbox.png"));
            ui.Click("nav-plugin-manager");ui.Find("plugins-list");ui.Plugin("qq-mail","1.0.0","QQ 邮箱");ui.Click("disable-plugin");ui.Result("已停用");
            string file=Path.Combine(home,"profiles/default/plugins.json");
            if(ConfigurationFiles.Read(file).Instances.Single(x=>x.Plugin=="qq-mail").Enabled)throw new Exception("Disable not saved");
            ui.Click("enable-plugin");ui.Result("插件已启用");if(!ConfigurationFiles.Read(file).Instances.Single(x=>x.Plugin=="qq-mail").Enabled)throw new Exception("Enable not saved");
            NativeWindowEvidence.CaptureClient(window, Path.Combine(output,"plugins.png"));
            ui.Click("nav-accounts/inbox");ui.SelectList("unified-list",0);
            ui.Click("unified-original");
            nint originalHandle=0;
            WaitFor(()=> (originalHandle=FindWindow(null,subjects[0]))!=0,"original message window");
            var original=automation.FromHandle(originalHandle).AsWindow();
            new Ui(original).Find("original-attachments");original.Close();
            ui.Click("nav-accounts/compose");ui.Text("compose-subject","会议纪要草稿");ui.Text("compose-body","请核对附件。");
            ui.Click("compose-save");ui.Click("nav-accounts/drafts");ui.ListContains("draft-list","会议纪要草稿");ui.SelectList("draft-list",0);ui.Click("draft-edit");
            WaitFor(()=>ui.Find("compose-subject").AsTextBox().Text=="会议纪要草稿","resume selected draft");
            NativeWindowEvidence.CaptureClient(window, Path.Combine(output,"compose.png"));
            ui.Click("nav-accounts/compose");WaitFor(()=>ui.Find("compose-subject").AsTextBox().Text=="","new mail should be blank");
            ui.Click("nav-accounts/outbox");ui.Find("outbox-list");ui.Click("nav-local-mail/sent");ui.Find("mail-list");
            ui.Click("nav-accounts/manage");ui.Find("account-name");
            ui.Click("nav-accounts/rules");ui.Text("rule-condition-value","会议");ui.Click("rule-condition-add");ui.Text("rule-name","周会");
            ui.Click("nav-accounts/automation");ui.Select("auto-condition-kind",1);ui.Find("auto-threshold");
            ui.Click("nav-accounts/inbox");
            ui.Text("inbox-search","报销");ui.Click("inbox-search-go");ui.ListContains("search-results","请核对报销明细");
            ui.Click("nav-accounts/inbox");ui.SelectList("unified-list",0);ui.Click("unified-Reply");
            ui.Click("compose-alternate");WaitFor(()=>ui.Find("compose-to").AsTextBox().Text.Contains("team@example.invalid"),"reply recipient");
            ui.Click("nav-accounts/inbox");
            var scale=window.BoundingRectangle.Width/1260d;
            window.Patterns.Transform.Pattern.Resize(620*Math.Clamp(scale,1,1.5),540*Math.Clamp(scale,1,1.5));
            ui.Find("compact-navigation");ui.SelectList("unified-list",0);WaitFor(()=>ui.Find("unified-body").AsTextBox().Text.Contains("周五"),"narrow reading");
            var bodyBounds = ui.Find("unified-body").BoundingRectangle;
            if (bodyBounds.Height < 60 || bodyBounds.Bottom > window.BoundingRectangle.Bottom)
                throw new Exception("窄窗口的邮件正文没有足够的阅读空间。");
            NativeWindowEvidence.CaptureClient(window, Path.Combine(output,"inbox-narrow.png"));
            ui.Click("unified-back"); ui.ListContains("unified-list","本周会议安排");
            NativeWindowEvidence.CaptureClient(window, Path.Combine(output,"inbox-narrow-list.png"));
            window.Close();WaitFor(()=>app.HasExited,"clean shutdown");
            Console.WriteLine("PASS default inbox, cached reading, blank compose and draft resume, sent and outbox, plug-in activation persistence, rule builder, task condition and compact layout");
        }
        catch
        {
            if(window!=null&&!app.HasExited)
            {
                File.WriteAllLines(Path.Combine(output,"controls.txt"),window.FindAllDescendants().Select(e=>$"{e.Properties.ControlType.ValueOrDefault} {e.Properties.AutomationId.ValueOrDefault} {e.Properties.Name.ValueOrDefault}"));
                NativeWindowEvidence.CaptureClient(window, Path.Combine(output,"failure.png"));
            }
            throw;
        }
        finally{if(!app.HasExited)app.Kill();}
    }
    internal static void NativeManagement(string output)
    {
        var home=Path.Combine(output,Guid.NewGuid().ToString("N"));
        var profile=Path.Combine(home,"profiles","default");
        MyMailBox.Builtins.DefaultProfile.Ensure(profile);
        var packages=new PluginPackages(Path.Combine(home,"plugins"),MyMailBox.Builtins.DefaultProfile.Catalog(home));
        var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        packages.Install(Path.Combine(repo,"artifacts","packages","native-1.0.0.zip"));
        var path=Path.Combine(profile,"plugins.json");
        var config=ConfigurationFiles.Read(path);
        ConfigurationFiles.Save(path,config with { Instances=[..config.Instances,new("native","native","1.0.0",Grants:["ui.native"])] });
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("MYMAILBOX_UI_EXE")!){UseShellExecute=false};
        start.Environment["MYMAILBOX_HOME"]=home;
        using var automation=new UIA3Automation();using var app=Application.Launch(start);
        try
        {
            var window=app.GetMainWindow(automation,TimeSpan.FromSeconds(30))??throw new Exception("No window");
            var ui=new Ui(window);ui.Click("nav-native/native");
            ui.Click("nav-plugin-manager");ui.Plugin("native","1.0.0","自定义窗口示例");ui.Click("disable-plugin");ui.Result("已停用");
            ui.Gone("nav-native/native");
            if(ConfigurationFiles.Read(path).Instances.Single(x=>x.Id=="native").Enabled)throw new Exception("Native disable was not saved");
            ui.Plugin("qq-mail","1.0.0","QQ 邮箱");ui.Click("disable-plugin");ui.Result("已停用");
            ui.Click("enable-plugin");ui.Result("插件已启用");
            window.Close();WaitFor(()=>app.HasExited,"native shutdown");
            Console.WriteLine("PASS live native feature removal, persisted disable, independent toggle of another plug-in, clean shutdown");
        }
        finally{if(!app.HasExited)app.Kill();}
    }

    private static void WaitFor(Func<bool> condition,string name)
    {if(!SpinWait.SpinUntil(condition,TimeSpan.FromSeconds(12)))throw new Exception(name);}
}
