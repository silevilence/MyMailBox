using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using MyMailBox.Contracts;

namespace MyMailBox.Desktop;

internal sealed partial class AutomationView
{
    private readonly List<TaskAction> _actions=[];
    private readonly ListBox _stepList=new(){MaxHeight=130};
    private readonly ComboBox _actionKind=new(),_actionFolder=new(),_failure=new(){ItemsSource=new[]{"失败后停止","失败后继续","有限重试"},SelectedIndex=0};
    private readonly NumericUpDown _tries=Number(3,1);
    private readonly TextBox _argument=new();
    private readonly CheckBox _confirmAction=new(){Content="我确认执行上次预览的删除影响"};
    private string? _actionApproval;
    private static string ActionName(TaskActionKind kind)=>kind switch
    {
        TaskActionKind.Extension=>"插件动作",TaskActionKind.Copy=>"复制",TaskActionKind.Read=>"标为已读",TaskActionKind.Unread=>"标为未读",TaskActionKind.Star=>"添加星标",
        TaskActionKind.Unstar=>"移除星标",TaskActionKind.Label=>"添加分类",TaskActionKind.SaveAttachments=>"保存附件",TaskActionKind.ExportEml=>"导出 EML",
        TaskActionKind.Notify=>"通知",TaskActionKind.Trash=>"移至垃圾箱",TaskActionKind.DeletePermanently=>"永久删除",_=>kind.ToString()
    };
    private void AddSteps()
    {
        var p=new StackPanel{Spacing=4};_actionKind.ItemsSource=Enum.GetValues<TaskActionKind>().Where(k=>k!=TaskActionKind.Extension).ToArray();
        _actionKind.ItemTemplate=new FuncDataTemplate<TaskActionKind>((v,_)=>new TextBlock{Text=ActionName(v)});_actionKind.SelectedIndex=0;
        Field(p,"动作",_actionKind,"auto-action-kind");Scope(_actionFolder);Field(p,"复制到目录",_actionFolder,"auto-action-destination");
        Field(p,"分类、通知文字或导出路径",_argument,"auto-action-text");Button(p,"选择导出目录","auto-action-directory",async()=>
        {
            var top=TopLevel.GetTopLevel(this);if(top is null)return;
            var list=await top.StorageProvider.OpenFolderPickerAsync(new(){Title="选择任务导出目录"});if(list.FirstOrDefault()?.TryGetLocalPath() is {} path)_argument.Text=path;
        });
        Field(p,"步骤失败时",_failure,"auto-action-failure");Field(p,"最多重试次数",_tries,"auto-step-retries");
        var edits=new WrapPanel();Button(edits,"添加步骤","auto-step-add",()=>
        {
            if(_actionKind.SelectedItem is not TaskActionKind kind)return Task.CompletedTask;
            var dest=kind==TaskActionKind.Copy?(_actionFolder.SelectedItem as SearchScope)?.Folder:null;
            _actions.Add(new(kind,dest,_argument.Text?.Trim()??"",(StepFailure)_failure.SelectedIndex,_failure.SelectedIndex==2?(int)(_tries.Value??1):0));ShowSteps();return Task.CompletedTask;
        });
        Button(edits,"上移","auto-step-up",()=>{int i=_stepList.SelectedIndex;if(i>0){(_actions[i],_actions[i-1])=(_actions[i-1],_actions[i]);ShowSteps();_stepList.SelectedIndex=i-1;}return Task.CompletedTask;});
        Button(edits,"移除步骤","auto-step-remove",()=>{if(_stepList.SelectedIndex>=0)_actions.RemoveAt(_stepList.SelectedIndex);ShowSteps();return Task.CompletedTask;});
        p.Children.Add(edits);Field(p,"执行顺序",_stepList,"auto-steps");
        Button(p,"预览动作影响","auto-actions-preview",async()=>
        {
            var preview=await _service.PreviewActions(Draft(),_stop.Token);_actionApproval=preview.Approval;_confirmAction.IsChecked=false;
            _status.Text=$"将处理 {preview.Selected} 封、{preview.Bytes:N0} 字节。\n"+string.Join("\n",preview.Effects.Select((e,i)=>$"{i+1}. {e}"));
        });
        p.Children.Add(_confirmAction);_panel.Children.Add(new Expander{Header="组合动作",Content=p});
    }
    private void ShowSteps(){_stepList.ItemsSource=_actions.Select((a,i)=>$"{i+1}. {ActionName(a.Kind)} {a.Extension?.Id??a.Argument}").ToArray();_confirmAction.IsChecked=false;}
    private void LoadSteps(AutomationPlan? p)
    {
        _actionFolder.ItemsSource=_library?.Scopes.Where(s=>s.Folder.HasValue).ToArray();_actions.Clear();_actions.AddRange(p?.Actions??[]);
        ShowSteps();_actionApproval=p?.Approval;_confirmAction.IsChecked=_actionApproval is not null;
    }
}
