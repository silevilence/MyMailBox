using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class AutomationView
{
    private readonly ComboBox _target=new(),_savedFilter=new(),_sort=new(){ItemsSource=new[]{"最旧优先","最大优先","最新优先"},SelectedIndex=0};
    private readonly CheckBox _useTarget=new(){Content="配置实际处理的邮件集合"},_descend=new(){Content="包括子目录"},_keepStars=new(){Content="排除星标邮件",IsChecked=true};
    private readonly TextBox _targetExpr=new(),_fixed=new(),_keepTag=new(){Text="保留"};
    private readonly NumericUpDown _age=Number(36500),_keep=Number(1000000),_maxItems=Number(10000,100),_maxBytes=Number(long.MaxValue),_water=Number(long.MaxValue);
    private readonly CheckBox _useWater=new(){Content="达到目标范围的低水位时停止"};
    private static NumericUpDown Number(decimal max,decimal value=0)=>new(){Minimum=0,Maximum=max,Value=value,Increment=1,FormatString="0"};
    private void AddTarget()
    {
        var p=new StackPanel{Spacing=4};p.Children.Add(_useTarget);Scope(_target);Field(p,"实际处理范围",_target,"auto-target");
        p.Children.Add(_descend);Field(p,"邮件过滤表达式",_targetExpr,"auto-target-filter");_savedFilter.ItemTemplate=new FuncDataTemplate<SavedSearch>((s,_)=>new TextBlock{Text=s?.Name});
        Field(p,"叠加保存搜索（可选）",_savedFilter,"auto-saved-search");Button(p,"清除保存搜索选择","auto-clear-saved",()=>{_savedFilter.SelectedIndex=-1;return Task.CompletedTask;});
        Field(p,"固定邮件 ID（空格或换行分隔，留空则动态选择）",_fixed,"auto-fixed");Field(p,"超过天数（0 表示不限）",_age,"auto-age");
        Field(p,"保留范围内最新封数",_keep,"auto-keep");p.Children.Add(_keepStars);Field(p,"排除带此标签的邮件",_keepTag,"auto-retention-tag");
        Field(p,"处理顺序",_sort,"auto-sort");Field(p,"每次最多封数",_maxItems,"auto-max-count");Field(p,"每次最多字节（0 表示不限）",_maxBytes,"auto-max-bytes");
        p.Children.Add(_useWater);Field(p,"低水位字节",_water,"auto-low-water");Button(p,"预览目标集合","auto-target-preview",async()=>{
            var s=await _service.Select(Draft(),token:_stop.Token);_status.Text=$"统计范围：{s.TriggerScope}\n实际处理范围：{s.TargetScope}\n{s.Items.Count} 封 · {s.SelectedBytes:N0} 字节 · {s.Detail}\n"+
                string.Join("\n",s.Items.Take(20).Select(m=>m.Subject+" · "+m.Id));});
        _panel.Children.Add(new Expander{Header="实际处理的邮件",Content=p});
    }
    private void LoadTarget(TargetSelection? t)
    {
        _useTarget.IsChecked=t is not null;_target.ItemsSource=_library?.Scopes;_target.SelectedItem=_library?.Scopes.FirstOrDefault(s=>s.Account==t?.Account&&s.Folder==t?.Folder);
        _savedFilter.ItemsSource=_library?.Saved;_savedFilter.SelectedItem=_library?.Saved.FirstOrDefault(s=>s.Id==t?.SavedSearch);
        _descend.IsChecked=t?.Descendants??false;_targetExpr.Text=t?.Filter??"";_fixed.Text=string.Join(" ",t?.Fixed??[]);
        _age.Value=t?.AgeDays??0;_keep.Value=t?.KeepNewest??0;_keepStars.IsChecked=t?.KeepStarred??true;_keepTag.Text=t?.KeepLabel??"保留";
        _sort.SelectedIndex=t?.Order switch{SearchOrder.Largest=>1,SearchOrder.Newest=>2,_=>0};
        _maxItems.Value=t?.MaxCount??100;_maxBytes.Value=t?.MaxBytes??0;_useWater.IsChecked=t?.LowWaterBytes.HasValue??false;_water.Value=t?.LowWaterBytes??0;
    }
    private TargetSelection? Target()
    {
        if(_useTarget.IsChecked!=true)return null;
        if(_target.SelectedItem is not SearchScope s)throw new InvalidDataException("请选择实际处理的范围。");
        return new(s.Account,s.Folder,_descend.IsChecked==true,_targetExpr.Text??"",(_savedFilter.SelectedItem as SavedSearch)?.Id,
            string.IsNullOrWhiteSpace(_fixed.Text)?null:_fixed.Text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries),
            (int)(_age.Value??0),(int)(_keep.Value??0),_keepStars.IsChecked==true,_keepTag.Text??"",_sort.SelectedIndex switch{1=>SearchOrder.Largest,2=>SearchOrder.Newest,_=>SearchOrder.Oldest},
            (int)(_maxItems.Value??0),(long)(_maxBytes.Value??0),_useWater.IsChecked==true?(long)(_water.Value??0):null);
    }
}
