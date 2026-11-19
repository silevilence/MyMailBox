using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed class SettingsTemplatesView : UserControl,IDisposable
{
    private readonly ISettingsTemplates _service;
    private readonly CancellationTokenSource _stop = new();
    private readonly StackPanel _form = new(){Spacing=8,MaxWidth=850},_mappings = new(){Spacing=5};
    private readonly TextBox _file = new();
    private readonly ComboBox _entries = new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly TextBlock _status = new(){TextWrapping=TextWrapping.Wrap};
    private readonly Dictionary<string,Func<string>> _values = [];
    private readonly Button _apply = new(){Content="导入所选条目",IsEnabled=false};
    private TemplateCatalog? _catalog;
    private string? _readyEntry;
    private bool _busy;
    internal SettingsTemplatesView(ISettingsTemplates service)
    {
        _service=service; _form.Children.Add(new TextBlock {Text="配置模板迁移",FontSize=28});
        _form.Children.Add(new TextBlock {Text="分享账户设置、保存的搜索、规则、任务和普通插件配置。账户凭据需在目标电脑重新授权。",TextWrapping=TextWrapping.Wrap});
        Field(_form,"模板 JSON 文件路径",_file,"path");
        _file.TextChanged+=(_,_)=>{ _catalog=null;_entries.ItemsSource=null;Reset(); };
        var files=new WrapPanel(); _form.Children.Add(files);
        Button(files,"导出到新文件","export",async ()=>
        {
            await service.Export(PathValue(),_stop.Token);
            _status.Text="配置模板已导出。可在目标工作空间打开此文件。";
        });
        Button(files,"打开模板","open",async ()=>
        {
            Reset(); _catalog=null; _entries.ItemsSource=null;
            _catalog=await service.Open(PathValue(),_stop.Token);_entries.ItemsSource=_catalog.Entries;
            _status.Text=$"共 {_catalog.Entries.Length} 个条目。选择一项后读取映射。";
        });
        _entries.ItemTemplate=new FuncDataTemplate<TemplateEntry>((e,_)=>new TextBlock{Text=e?.Name});
        Field(_form,"待迁移条目",_entries,"entries"); _entries.SelectionChanged+=(_,_)=>Reset();
        Button(_form,"读取本机映射选项","fields",async ()=>
        {
            Reset();var entry=Entry();
            foreach(var field in await service.Fields(PathValue(),_catalog!.Digest,entry,_stop.Token))
            {
                if(field.Choices is {} choices)
                {
                    var combo=new ComboBox{ItemsSource=choices,HorizontalAlignment=HorizontalAlignment.Stretch};
                    combo.ItemTemplate=new FuncDataTemplate<Choice>((c,_)=>new TextBlock{Text=c?.Label});
                    combo.SelectionChanged+=(_,_)=>_apply.IsEnabled=false;
                    Field(_mappings,field.Label,combo,"map-"+_values.Count);
                    _values.Add(field.Key,()=> (combo.SelectedItem as Choice)?.Id ?? "");
                }
                else
                {
                    var box=new TextBox{Text=field.Initial};box.TextChanged+=(_,_)=>_apply.IsEnabled=false;
                    Field(_mappings,field.Label,box,"map-"+_values.Count);_values.Add(field.Key,()=>box.Text??"");
                }
            }
            _readyEntry=entry;_status.Text="核对本机绑定后预览。规则、任务与新插件实例以停用状态导入。";
        });
        _form.Children.Add(_mappings);
        Button(_form,"预览映射结果","preview",async ()=>
        {
            _apply.IsEnabled=false;
            _status.Text=await Review(false); _apply.IsEnabled=true;
        });
        AutomationProperties.SetAutomationId(_apply,"templates-apply"); _apply.Click+=async(_,_)=>await Run(async()=>
        {
            _status.Text="导入完成。"+await Review(true);Reset();
        });
        _form.Children.Add(_apply);
        AutomationProperties.SetAutomationId(_status,"templates-status");Content=PageFeedback.Layout(_form,_status);
    }
    private async Task<string> Review(bool save)
    {
        var entry=Entry();
        if(entry!=_readyEntry)throw new InvalidDataException("请先读取所选条目的映射选项。");
        return await _service.Review(PathValue(),_catalog!.Digest,entry,_values.ToDictionary(k=>k.Key,k=>k.Value()),save,_stop.Token);
    }
    private string PathValue()=>!string.IsNullOrWhiteSpace(_file.Text)?_file.Text.Trim():throw new InvalidDataException("请填写配置模板的文件路径。");
    private string Entry()=>_catalog!=null && _entries.SelectedItem is TemplateEntry e?e.Key:throw new InvalidDataException("请打开模板并选择条目。");
    private void Reset(){_readyEntry=null;_apply.IsEnabled=false;_values.Clear();_mappings.Children.Clear();}
    private static void Field(Panel p,string label,Control c,string id)
    {
        p.Children.Add(new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap});p.Children.Add(c);
        AutomationProperties.SetName(c,label);AutomationProperties.SetAutomationId(c,"templates-"+id);
    }
    private void Button(Panel panel,string label,string id,Func<Task> action)
    {
        var button=new Button{Content=label,Margin=new Avalonia.Thickness(0,0,6,3)};
        AutomationProperties.SetAutomationId(button,"templates-"+id);button.Click+=async(_,_)=>await Run(action);panel.Children.Add(button);
    }
    private async Task Run(Func<Task> work)
    {
        if(_busy || _stop.IsCancellationRequested)return;
        _busy=true;_form.IsEnabled=false;
        try { await work(); } catch(OperationCanceledException) { }
        catch(Exception e){ _apply.IsEnabled=false;_status.Text=e is InvalidDataException or InvalidOperationException or IOException or FormatException?e.Message:"配置迁移未完成，请核对模板和本机权限。"; }
        finally { _busy=false;_form.IsEnabled=true; }
    }
    public void Dispose(){_stop.Cancel();_stop.Dispose();}
}
