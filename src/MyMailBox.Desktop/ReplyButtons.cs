using Avalonia.Automation;
using Avalonia.Controls;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal static class ReplyButtons
{
    public static void Add(Panel panel,string prefix,Func<string?> key,Action<ReplyIntent>? open,Action<string> feedback)
    {
        if(open is null)return;
        foreach(var (mode,name) in new[]{(ReplyMode.Reply,"回复"),(ReplyMode.ReplyAll,"回复全部"),(ReplyMode.Forward,"转发")})
        {
            var button=new Button{Content=name,Margin=new Avalonia.Thickness(0,0,6,2)};
            AutomationProperties.SetAutomationId(button,prefix+"-"+mode);
            button.Click+=(_,_)=>{if(key() is {} source)open(new(source,mode));else feedback("请先选择并打开一封邮件。");};panel.Children.Add(button);
        }
    }
}
