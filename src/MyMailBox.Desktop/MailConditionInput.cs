using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

// Keep the expression as the persisted value so previously saved and compound rules round-trip.
internal static class MailConditionInput
{
    internal static void Add(Panel panel,string title,TextBox expression,string id)
    {
        panel.Children.Add(new TextBlock { Text=title, FontWeight=FontWeight.Medium });
        var fields=new ComboBox { ItemsSource=new Choice[]{new("subject","主题包含"),new("from","发件人包含"),new("body","正文包含"),new("to","收件人包含")},
            ItemTemplate=new FuncDataTemplate<Choice>((c,_)=>new TextBlock{Text=c?.Label}), SelectedIndex=0 };
        var words=new TextBox { PlaceholderText="输入文字",MinWidth=120 };
        var add=new Button { Content="添加条件" };
        var inputs=new WrapPanel();inputs.Children.Add(fields);inputs.Children.Add(words);inputs.Children.Add(add);panel.Children.Add(inputs);
        var summary=new TextBlock {TextWrapping=TextWrapping.Wrap};panel.Children.Add(summary);
        var advanced=new StackPanel { Spacing=4 };advanced.Children.Add(expression);
        var clear=new Button {Content="清空条件"};advanced.Children.Add(clear);clear.Click+=(_,_)=>expression.Text="";
        var expander=new Expander { Header="编辑条件表达式",Content=advanced };panel.Children.Add(expander);
        AutomationProperties.SetAutomationId(expander,id+"-advanced");
        foreach(var (control,key,name) in new (Control,string,string)[]{(fields,"-field","比较内容"),(words,"-value","匹配文字"),(add,"-add","添加条件"),(expression,"","条件表达式")})
        { AutomationProperties.SetAutomationId(control,id+key);AutomationProperties.SetName(control,title+" · "+name); }
        add.Click+=(_,_)=>{
            if(fields.SelectedItem is not Choice c || string.IsNullOrWhiteSpace(words.Text)) return;
            var leaf=ConditionEditor.Expression(ConditionEditor.Leaf(new(c.Id,SearchMatch.Contains,words.Text.Trim())));
            expression.Text=string.IsNullOrWhiteSpace(expression.Text)?leaf:"("+expression.Text+") AND "+leaf;
            words.Text="";
        };
        expression.TextChanged+=(_,_)=>{
            try {summary.Text=string.IsNullOrWhiteSpace(expression.Text)?"":ConditionEditor.Describe(ConditionTree.Parse(expression.Text).Root,
                fields.Items.OfType<Choice>().Select(c=>new SearchField(c.Id,c.Label.Replace("包含",""),SearchKind.Text,[SearchMatch.Contains])).ToArray());}
            catch(Exception error) when(error is FormatException or InvalidDataException){summary.Text="条件尚未写完整。";}
        };
    }
}
