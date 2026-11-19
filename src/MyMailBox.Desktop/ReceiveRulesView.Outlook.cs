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
        void Display(OutlookRuleInventory value)
        {
            inventory = value;
            choices.ItemsSource = value.Rules.OrderBy(r=>r.Order).ToArray();
            choices.SelectedIndex = value.Rules.Length > 0 ? 0 : -1;
            _status.Text = $"{value.Profile} / {value.Store} · {value.Rules.Length} 条规则。";
        }
        var stores = new ComboBox();

        Button(form,"查看已打开的 Outlook 邮箱","outlook-rule-list-stores",async () =>
        {
            stores.ItemsSource = null; choices.ItemsSource = null; inventory = null;
            var available = await ClassicOutlookRules.Stores(_stop.Token);
            stores.ItemsSource = available;
            stores.SelectedIndex = available.Length == 1 ? 0 : -1;
            _status.Text = available.Length > 0 ? "请选择要读取规则的邮箱。" : "经典 Outlook 中没有可用邮箱。";
        });
        stores.SelectionChanged += (_, _) => { choices.ItemsSource = null; inventory = null; detail.Text = ""; };
        AddField("Outlook 邮箱",stores,"store");
        Button(form,"读取所选邮箱的规则","outlook-rule-read-store",async () =>
        {
            if (stores.SelectedItem is not OutlookRuleStore store) throw new InvalidDataException("请先选择 Outlook 邮箱。");
            Display(await ClassicOutlookRules.Read(store,_stop.Token));
        });
        AddField("或选择已导出的规则文件",file,"file");
        Button(form,"选择规则文件…","outlook-rule-browse",async()=>{var picker=TopLevel.GetTopLevel(this)?.StorageProvider;if(picker is null)return;
            var selected=await picker.OpenFilePickerAsync(new(){Title="选择 Outlook 规则清单"});if(selected.FirstOrDefault() is {} chosen)file.Text=chosen.Path.LocalPath;});
        file.TextChanged += (_,_) => { inventory = null; choices.ItemsSource = null; detail.Text = ""; };
        Button(form,"读取清单","outlook-rule-open",async () =>
        {
            choices.ItemsSource = null; inventory = null;
            Display(await OutlookRuleImport.Read(file.Text?.Trim() ?? "",_stop.Token));
        });
        AddField("源规则",choices,"source");
        AddField("应用到本机账户或目录",_outlookScope,"scope");
        AddField("邮件要移入的本地文件夹",_outlookTarget,"destination");
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
        var expander = new Expander { Header = "导入经典 Outlook 规则", Content = form };
        AutomationProperties.SetAutomationId(expander,"outlook-rule-import"); _panel.Children.Add(expander);
    }
}
