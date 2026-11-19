using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using MyMailBox.Contracts;
using MyMailBox.Runtime;
using System.Net;

namespace MyMailBox.Desktop;

internal sealed partial class ComposerView : UserControl,IDisposable
{
    private readonly ComposerPage _page;
    private readonly Contributions _contributions;
    private readonly string _owner;
    private readonly CancellationTokenSource _stop=new();
    private readonly StackPanel _form=new(){Spacing=6,MaxWidth=840};
    private readonly ComboBox _accounts=new(),_drafts=new(),_editors=new(),_templates=new();
    private readonly TextBox _to=new(),_cc=new(),_bcc=new(),_subject=new(),_plain=new(){AcceptsReturn=true,MinHeight=148,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _identity=new(),_status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly ContentControl _richHost=new();
    private RichComposeView? _rich;
    private readonly StackPanel _cloudDraft = new();
    private MailDraft? _draft;
    private ReplyIntent? _intent;
    private readonly StackPanel _alternate=new();
    private readonly CheckBox _include=new(){Content="发送时包含引用",IsChecked=true};
    private readonly TextBlock _quoteText=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Expander _quote=new(){Header="引用原邮件"};
    private ComposerEditor? _editor;
    private bool _binding,_busy,_disposed;
    private IDisposable? _preparation;

    internal ComposerView(ComposerPage page,Contributions contributions,string owner,ReplyIntent? intent=null)
    {
        _page=page;_contributions=contributions;_owner=owner;_intent=intent;
        _form.Children.Add(new TextBlock{Text="写邮件与草稿",FontSize=28,FontWeight=FontWeight.SemiBold});
        _accounts.ItemTemplate=new FuncDataTemplate<MailAccount>((a,_)=>new TextBlock{Text=a?.Name+" · "+a?.Address});
        _drafts.ItemTemplate=new FuncDataTemplate<MailDraft>((d,_)=>new TextBlock{Text=string.IsNullOrWhiteSpace(d?.Subject)?"（无主题草稿）":d.Subject});
        _editors.ItemTemplate=new FuncDataTemplate<ComposerEditor>((e,_)=>new TextBlock{Text=e?.Name});
        _templates.ItemTemplate=new FuncDataTemplate<EditorTemplate>((t,_)=>new TextBlock{Text=t?.Name});
        Field("发件账户",_accounts,"compose-account");
        Button(_alternate,"用所选账户继续回复或转发","compose-alternate",CreateResponse);_alternate.IsVisible=false;_form.Children.Add(_alternate);_form.Children.Add(_identity);AddProfile();
        Field("该账户的草稿",_drafts,"compose-drafts");
        var draftTools=new WrapPanel();Button(draftTools,"打开所选草稿","compose-open",async()=>{await Flush();if(_drafts.SelectedItem is MailDraft d)Load(_page.Drafts.List(d.Account).Single(x=>x.Id==d.Id));});
        Button(draftTools,"新建草稿","compose-new",async()=>{await Flush();var a=Account();Load(MailDraft.New(a.Id,a.Address));});_form.Children.Add(draftTools);
        if (page.Graph is { } graph)
        {
            Button(_cloudDraft, "将当前版本另存为云端草稿", "compose-graph-draft", async () =>
            {
                await Flush();
                var draft = _draft ?? throw new InvalidDataException("请先填写并保存草稿。");
                await graph.SaveDraft(draft, _stop.Token);
                _status.Text = "当前版本已保存到微软草稿目录。本地草稿可继续编辑。";
            });
            _cloudDraft.IsVisible = false;
            _form.Children.Add(_cloudDraft);
        }
        Field("收件人 To",_to,"compose-to");Field("抄送 Cc",_cc,"compose-cc");Field("密送 Bcc",_bcc,"compose-bcc");AddAddresses();Field("主题",_subject,"compose-subject");
        Field("编辑器",_editors,"compose-editor");var formats=new WrapPanel();
        Button(formats,"编辑纯文本（保留富文本副本）","compose-plain",()=>Format(false));
        Button(formats,"打开富文本副本","compose-rich",()=>Format(true));
        Button(formats,"从当前纯文本重建富文本","compose-convert",()=>Format(true,true));_form.Children.Add(formats);
        Field("正文",_plain,"compose-body");_form.Children.Add(_richHost);
        var quotePanel=new StackPanel();quotePanel.Children.Add(_include);quotePanel.Children.Add(_quoteText);_quote.Content=quotePanel;_quote.IsVisible=false;_form.Children.Add(_quote);
        _include.IsCheckedChanged+=(_,_)=>{if(!_binding && _draft is {Quote:{} q} d)Persist(d with{Quote=q with{Included=_include.IsChecked==true}});};
        AddFiles();AddSendPlan();
        Field("模板",_templates,"compose-template");
        var tools=new WrapPanel();Button(tools,"插入模板","compose-insert",InsertTemplate);
        Button(tools,"拼写检查","compose-spell",async()=>{await Flush();if(_draft is {} d && _editor is {} e)_status.Text=await e.Spell(d.Body.Plain,_stop.Token);});
        Button(tools,"保存草稿","compose-save",Flush);Button(tools,"发送","compose-send",Send);_form.Children.Add(tools);
        AutomationProperties.SetAutomationId(_status,"compose-status");_form.Children.Add(_status);Content=PageFeedback.Layout(_form,_status);
        foreach(var box in new[]{_to,_cc,_bcc,_subject,_plain})box.TextChanged+=(_,_)=>{if(!_binding && !_busy)TrySave();};
        _accounts.SelectionChanged+=async(_,_)=>{if(!_binding)await Guard(SwitchAccount);};
        _editors.SelectionChanged+=async(_,_)=>{if(!_binding)await Guard(SwitchEditor);};
        contributions.Changed+=EditorsChanged;contributions.BeforePluginStop+=BeforeStop;
        _preparation=contributions.AddUpdatePreparation(new("composer/"+Guid.NewGuid(),async _=>await Dispatcher.UIThread.InvokeAsync(Flush)));
        AttachedToVisualTree+=async(_,_)=>await Guard(async()=>{
            _binding=true;try{_accounts.ItemsSource=(await page.Accounts.List(_stop.Token)).Where(a=>a.Enabled && a.Capabilities.HasFlag(AccountCapabilities.Send)).ToArray();
                _accounts.SelectedIndex=_accounts.ItemCount>0?0:-1;SetEditors();}finally{_binding=false;}
            if(_intent is {} reply)
            {
                var source=page.Drafts.SourceAccount(reply.Key);
                var preferred=_accounts.Items.OfType<MailAccount>().FirstOrDefault(a=>a.Id==source);
                if(preferred is not null){_binding=true;_accounts.SelectedItem=preferred;_binding=false;await CreateResponse();}
                else{_alternate.IsVisible=true;_status.Text="原接收账户无法发送，请明确选择替代账户再继续。";}
            }
            else await SwitchAccount();
        });
    }

    private static readonly ComposerEditor Builtin=new("builtin","内置富文本编辑器",true,[new("问候","您好，")],b=>b,
        (_,_)=>Task.FromResult("富文本编辑区已启用系统拼写检查，可用上下文菜单查看建议。"));
    private void SetEditors()
    {
        var id=_editor?.Id;var all=new[]{Builtin}.Concat(_contributions.Editors).ToArray();
        SetAddressProviders();_editors.ItemsSource=all;_editor=all.FirstOrDefault(e=>e.Id==id) ?? Builtin;_editors.SelectedItem=_editor;
        _templates.ItemsSource=_editor.Templates;_templates.SelectedIndex=_templates.ItemCount>0?0:-1;
    }
    private void EditorsChanged()
    {
        if(_disposed)return;bool old=_binding;_binding=true;try{SetEditors();}finally{_binding=old;}
    }
    private async Task BeforeStop(string instance)
    {
        await Dispatcher.UIThread.InvokeAsync(async()=>{
            if(_disposed || instance!=_owner && _editor?.Id.StartsWith(instance+"/",StringComparison.Ordinal)!=true)return;
            await Flush();CloseRich();_editor=Builtin;
            if(_draft is {} d && d.Body.Rich)Persist(d with{Body=d.Body with{Rich=false}});
            _plain.Text=_draft?.Body.Plain ?? "";_plain.IsVisible=true;
            _status.Text="编辑器已保存并关闭，草稿可由其他编辑器接管。";
        });
    }
    private void Field(string label,Control element,string id)
    {
        _form.Children.Add(new TextBlock{Text=label});AutomationProperties.SetName(element,label);AutomationProperties.SetAutomationId(element,id);_form.Children.Add(element);
    }
    private void Button(Panel panel,string label,string id,Func<Task> work)
    {
        var b=new Button{Content=label,Margin=new Avalonia.Thickness(0,0,6,2)};AutomationProperties.SetAutomationId(b,id);
        b.Click+=async(_,_)=>await Guard(work);panel.Children.Add(b);
    }
    private async Task Guard(Func<Task> work)
    {
        if(_busy || _disposed)return;_busy=true;
        try{await work();}catch(OperationCanceledException){}catch(Exception e){_status.Text="未完成："+e.Message;}finally{_busy=false;}
    }
    private MailAccount Account()=>_accounts.SelectedItem as MailAccount ?? throw new InvalidDataException("请先配置有发送能力的账户。");
    private void ListDrafts(Guid account)
    {
        var rows=_page.Drafts.List(account);_drafts.ItemsSource=rows;_drafts.SelectedItem=rows.FirstOrDefault(d=>d.Id==_draft?.Id) ?? rows.FirstOrDefault();
    }
    private async Task CreateResponse()
    {
        if(_intent is not {} intent)return;var a=Account();
        var draft=await _page.Drafts.Respond(intent,a.Id,_stop.Token);_intent=null;_alternate.IsVisible=false;LoadProfile(a);ListDrafts(a.Id);Load(draft);
    }
    private async Task SwitchAccount()
    {
        _suggesting?.Cancel();_suggestions.IsVisible=false;_recipient=null;
        await Flush();if(_accounts.SelectedItem is not MailAccount a){_status.Text="请先配置可发送邮件的账户。";return;}
        if(_intent is not null){_status.Text="请确认替代发件账户。";return;}
        LoadProfile(a);ListDrafts(a.Id);Load((_drafts.SelectedItem as MailDraft) ?? MailDraft.New(a.Id,a.Address));
    }
    private async Task SwitchEditor()
    {
        if(_editors.SelectedItem is not ComposerEditor next || _draft is null)return;
        await Flush();var body=next.Open(_draft.Body);_editor=next;
        _templates.ItemsSource=next.Templates;_templates.SelectedIndex=_templates.ItemCount>0?0:-1;
        if(!next.SupportsRich)body=body with{Rich=false};
        if(body!=_draft.Body)_draft=_page.Drafts.Save(_draft with{Body=body});
        Load(_draft);
    }
    private void Load(MailDraft d)
    {
        _cloudDraft.IsVisible = _page.Graph != null && (_accounts.SelectedItem as MailAccount)?.Provider == "outlook-com";
        _binding=true;try{
            CloseRich();_draft=d;_to.Text=d.To;_cc.Text=d.Cc;_bcc.Text=d.Bcc;_subject.Text=d.Subject;_plain.Text=d.Body.Plain;_identity.Text="发件身份："+d.Identity;_from.SelectedItem=d.Identity;_files.ItemsSource=d.Parts;_quote.IsVisible=d.Quote is not null;_quoteText.Text=d.Quote is {} q ? q.Heading+"\n"+q.Text : "";_include.IsChecked=d.Quote?.Included==true;
            _plain.IsVisible=!d.Body.Rich; if(d.Body.Rich)OpenRich(d);_status.Text=d.Version>0?"已恢复草稿。":"新草稿。";if(_planId!=d.Id)_=ReadPlan(d.Id);
        }finally{_binding=false;}
    }
    private void CloseRich(){_rich?.Dispose();_rich=null;_richHost.Content=null;}
    private void OpenRich(MailDraft d)
    {
        var view=new RichComposeView(d.Body,body=>{if(_draft?.Id==d.Id && _draft.Body.Rich)Persist(_draft with{Body=body});},s=>_status.Text=s,()=>_status.Text="富文本编辑器已就绪。",cid=>d.Parts.FirstOrDefault(p=>p.Cid==cid) is {} p ? (p.Mime,_page.Drafts.PartData(d.Account,p.Id)) : null){Height=205};
        _rich=view;_richHost.Content=view;
    }
    private void TrySave(){try{Capture();}catch(Exception e){_status.Text="尚未保存："+e.Message;}}
    private void Capture()
    {
        if(_draft is not {} d)return;
        var body=d.Body.Rich?d.Body:d.Body with{Plain=_plain.Text ?? ""};
        Persist(d with{To=_to.Text ?? "",Cc=_cc.Text ?? "",Bcc=_bcc.Text ?? "",Subject=_subject.Text ?? "",Body=body});
    }
    private void Persist(MailDraft next)
    {
        if(_draft==next)return;bool first=next.Version==0;_draft=_page.Drafts.Save(next);
        _status.Text="草稿已保存 · "+DateTime.Now.ToString("T");if(first)ListDrafts(next.Account);
    }
    private async Task Flush(){if(_rich is {} view)await view.Flush();Capture();}
    private async Task Format(bool rich,bool replace=false)
    {
        await Flush();if(_draft is not {} d)return;
        if(rich && _editor?.SupportsRich==false)throw new InvalidDataException("此编辑器只支持纯文本，请选择富文本编辑器。");
        var body=d.Body;
        if(rich && !replace && body.Html.Length>0 && body.Plain!=body.RichText)
            throw new InvalidDataException("纯文本已有修改。可保留当前格式，或使用“从当前纯文本重建富文本”转换。");
        if(rich && (replace || body.Html.Length==0))body=body with{Html="<div>"+WebUtility.HtmlEncode(body.Plain).Replace("\n","<br>")+"</div>",RichText=body.Plain};
        Persist(d with{Body=body with{Rich=rich}});Load(_draft!);
    }
    private async Task InsertTemplate()
    {
        await Flush();if(_draft is not {} d || _templates.SelectedItem is not EditorTemplate t)return;
        string plain=d.Body.Plain+"\n"+t.Text;
        var body=d.Body.Rich?new DraftBody(plain,d.Body.Html+"<p>"+WebUtility.HtmlEncode(t.Text)+"</p>",plain,true):d.Body with{Plain=plain};
        Persist(d with{Body=body});Load(_draft!);
    }
    private async Task Send()
    {
        await Flush();var d=_draft ?? throw new InvalidDataException("请先创建草稿。");
        var a=Account();if(d.Account!=a.Id || !_page.Drafts.Profile(a.Id).Aliases.Append(a.Address).Contains(d.Identity,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("发件身份已变更，请重新选择当前身份再发送。");
        if(d.Version==0)_draft=d=_page.Drafts.Save(d);
        await _page.Outbox.Enqueue(d.Id,a.Id,d.Message(),_stop.Token);_page.Drafts.Submitted(d.Id,d.Version);
        CloseRich();_draft=null;ListDrafts(a.Id);Load(MailDraft.New(a.Id,a.Address));
        var result=await _page.Outbox.Deliver(d.Id,_stop.Token);_status.Text=result.Message;
    }
    internal async Task<bool> SaveBeforeLeaving()
    {
        try { await Flush(); return true; }
        catch(Exception e) { _status.Text="尚未保存："+e.Message;return false; }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_suggesting?.Cancel();_contributions.Changed-=EditorsChanged;_contributions.BeforePluginStop-=BeforeStop;
        _preparation?.Dispose();CloseRich();_stop.Cancel();_stop.Dispose();
    }
}
