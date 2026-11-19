using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MyMailBox.Contracts;
using MyMailBox.Mail;

namespace MyMailBox.Desktop;

internal sealed partial class ReceiveRulesView
{
    private readonly ComboBox _outlookScope = new(), _outlookTarget = new();
    private void AddOutlookImport()
    {
        var file = new TextBox();
        var choices = new ComboBox();
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var form = new StackPanel { Spacing = 5 };
        OutlookRuleInventory? inventory = null;
        choices.ItemTemplate = new FuncDataTemplate<OutlookSourceRule>((r,_)=>new TextBlock { Text = r == null ? "" : $"{r.Order} · {r.Name} · {(r.SourceEnabled ? "源已启用" : "源已停用")}" });
        _outlookTarget.ItemTemplate = _outlookScope.ItemTemplate = new FuncDataTemplate<SearchScope>((s,_)=>new TextBlock { Text = s?.Name });
        void AddField(string label,Control control,string id)
        {
            form.Children.Add(new TextBlock { Text = label }); form.Children.Add(control);
            AutomationProperties.SetName(control,label); AutomationProperties.SetAutomationId(control,"outlook-rule-"+id);
        }
        AddField("规则清单 JSON 文件",file,"file");
        file.TextChanged += (_,_) => { inventory = null; choices.ItemsSource = null; detail.Text = ""; };
        Button(form,"读取清单","outlook-rule-open",async () =>
        {
            choices.ItemsSource = null; inventory = null;
            inventory = await OutlookRuleImport.Read(file.Text?.Trim() ?? "",_stop.Token);
            choices.ItemsSource = inventory.Rules.OrderBy(r=>r.Order).ToArray(); choices.SelectedIndex = 0;
            _status.Text = $"{inventory.Profile} / {inventory.Store} · {inventory.Rules.Length} 条规则。";
        });
        AddField("源规则",choices,"source");
        AddField("应用到本机账户或目录",_outlookScope,"scope");
        AddField("将这条规则的移动目标映射到",_outlookTarget,"destination");
        _outlookScope.SelectionChanged += (_,_) =>
        {
            _outlookTarget.ItemsSource = _dashboard?.Scopes.Where(s => s.Folder != null && s.Account == (_outlookScope.SelectedItem as SearchScope)?.Account).ToArray();
            _outlookTarget.SelectedIndex = -1; Describe();
        };
        choices.SelectionChanged += (_,_) => Describe();
        _outlookTarget.SelectionChanged += (_,_) => Describe();
        void Describe()
        {
            if (inventory == null || choices.SelectedItem is not OutlookSourceRule rule) { detail.Text = ""; return; }
            var mapped = OutlookRuleImport.Convert(inventory,rule,_outlookScope.SelectedItem as SearchScope ?? new(null,null,""),_outlookTarget.SelectedItem as SearchScope);
            detail.Text = string.Join(Environment.NewLine,mapped.Report);
        }
        AutomationProperties.SetAutomationId(detail,"outlook-rule-report"); form.Children.Add(detail);
        Button(form,"对照本机邮件并载入停用草稿","outlook-rule-convert",async () =>
        {
            if (inventory == null || choices.SelectedItem is not OutlookSourceRule rule || _outlookScope.SelectedItem is not SearchScope scope)
                throw new InvalidDataException("请读取清单，选择源规则和本机适用范围。");
            var result = OutlookRuleImport.Convert(inventory,rule,scope,_outlookTarget.SelectedItem as SearchScope);
            detail.Text = string.Join(Environment.NewLine,result.Report);
            if (result.Rule == null) { _status.Text = "此规则包含未映射项目。请参照逐项报告手动迁移。"; return; }
            var saved = (await _service.Dashboard(_stop.Token)).Rules.FirstOrDefault(r => r.Id == result.Rule.Id);
            if (saved != null) { Edit(saved); _status.Text = "已载入此前导入的规则。"; return; }
            var preview = await _service.Preview(result.Rule,_stop.Token);
            Edit(result.Rule);
            detail.Text += Environment.NewLine + string.Join(Environment.NewLine,preview.Samples);
            _status.Text = $"已载入停用草稿 · 命中 {preview.Matches} 封，例外 {preview.Exceptions} 封，未知 {preview.Unknown} 封。核对后可保存。";
        });
        var expander = new Expander { Header = "从经典 Outlook 规则清单转换", Content = form };
        AutomationProperties.SetAutomationId(expander,"outlook-rule-import"); _panel.Children.Add(expander);
    }
}
