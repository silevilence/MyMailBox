using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class AutomationView
{
    private readonly ExtensionEditor _pluginGate=new(AutomationPart.Condition),_pluginAction=new(AutomationPart.Action),_pluginTimer=new(AutomationPart.Schedule);
    private void AddExtensions()
    {
        var p=new StackPanel{Spacing=6};_pluginGate.Attach(p,"条件扩展");_pluginTimer.Attach(p,"调度扩展");_pluginAction.Attach(p,"动作扩展");
        Button(p,"添加扩展步骤","auto-extension-add",()=>
        {
            var binding=_pluginAction.Read()??throw new InvalidDataException("请选择动作扩展并填写参数。");
            _actions.Add(new(TaskActionKind.Extension){Extension=binding});ShowSteps();return Task.CompletedTask;
        });
        _panel.Children.Add(new Expander{Header="插件条件、动作与调度",Content=p});
    }
    private void LoadExtensions(AutomationPlan? plan)
    {
        var all=_service.Extensions;_pluginGate.Load(all,plan?.PluginCondition);_pluginTimer.Load(all,plan?.PluginSchedule);_pluginAction.Load(all,null);
    }
    private sealed class ExtensionEditor(AutomationPart part)
    {
        private sealed record Option(string Name,AutomationDescriptor? Descriptor,AutomationBinding? Missing=null);
        private readonly ComboBox _choice=new();private readonly StackPanel _fields=new(){Spacing=4};
        private readonly Dictionary<string,TextBox> _values=[];private AutomationBinding? _saved;private bool _edited,_loading;
        public void Attach(Panel p,string label)
        {
            _choice.ItemTemplate=new FuncDataTemplate<Option>((o,_)=>new TextBlock{Text=o?.Name});Field(p,label,_choice,"auto-plugin-"+part);
            p.Children.Add(_fields);_choice.SelectionChanged+=(_,_)=>{if(!_loading){_saved=null;Build();}};
        }
        internal void Load(IReadOnlyList<AutomationDescriptor> all,AutomationBinding? binding)
        {
            _loading=true;var choices=new List<Option>{new("未启用",null)};choices.AddRange(all.Where(d=>d.Part==part).Select(d=>new Option(d.Name+" · v"+d.Version,d)));
            if(binding is not null && choices.All(o=>o.Descriptor?.Id!=binding.Id))choices.Add(new("已停用："+binding.Id,null,binding));
            _choice.ItemsSource=choices;_choice.SelectedItem=choices.FirstOrDefault(o=>binding is not null&&(o.Descriptor?.Id==binding.Id||o.Missing==binding))??choices[0];
            _saved=binding;_loading=false;Build();
        }
        private void Build()
        {
            _edited=false;_values.Clear();_fields.Children.Clear();if(_choice.SelectedItem is not Option{Descriptor:{} d})return;
            if(_saved is {} b&&b.Version!=d.Version)_fields.Children.Add(new TextBlock{Text=$"原配置 v{b.Version}。保存时尝试迁移，保留原参数直到成功。"});
            foreach(var a in d.Parameters)
            {
                var box=new TextBox{Text=_saved?.Parameters.GetValueOrDefault(a.Id)??a.DefaultValue};
                Field(_fields,a.Name+(a.Number?"（数值）":""),box,"auto-plugin-"+part+"-"+a.Id);_values.Add(a.Id,box);
                box.TextChanged+=(_,_)=>_edited=true;
            }
        }
        internal AutomationBinding? Read()
        {
            if(_choice.SelectedItem is not Option o)return null;
            if(o.Missing is not null)return o.Missing;
            if(o.Descriptor is not {} d)return null;
            if(!_edited&&_saved is not null)return _saved;
            return new(d.Id,d.Version,_values.ToDictionary(k=>k.Key,k=>k.Value.Text??""));
        }
    }
}
