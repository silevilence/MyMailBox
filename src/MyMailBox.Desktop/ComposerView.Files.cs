using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MyMailBox.Contracts;
using System.Net;

namespace MyMailBox.Desktop;

internal sealed partial class ComposerView
{
    private readonly ComboBox _from=new();
    private readonly TextBox _signature=new(){AcceptsReturn=true},_aliases=new();
    private readonly NumericUpDown _limit=new(){Minimum=1,Maximum=25*1024*1024,Increment=1024,FormatString="0"};
    private readonly ListBox _files=new(){MaxHeight=130};
    private CancellationTokenSource? _adding;

    private void AddProfile()
    {
        Field("可用发件身份",_from,"compose-from");
        _from.SelectionChanged+=(_,_)=>{if(!_binding && !_busy && _draft is {} d && _from.SelectedItem is string address)try{Persist(d with{Identity=address});_identity.Text="发件身份："+address;}catch(Exception e){_status.Text=e.Message;}};
        var panel=new StackPanel{Spacing=4};panel.Children.Add(new TextBlock{Text="服务商已授权的备用发件地址（逗号分隔）"});panel.Children.Add(_aliases);
        panel.Children.Add(new TextBlock{Text="账户签名"});panel.Children.Add(_signature);panel.Children.Add(new TextBlock{Text="整封邮件上限（字节）"});panel.Children.Add(_limit);
        Button(panel,"保存账户发送设置","compose-profile-save",()=>{
            var a=Account();_page.Drafts.Configure(a.Id,new(_signature.Text ?? "",(_aliases.Text ?? "").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries),(long)(_limit.Value ?? 1)));
            LoadProfile(a);_from.SelectedItem=_draft?.Identity;_status.Text="已保存账户设置。";return Task.CompletedTask;});
        _form.Children.Add(new Expander{Header="签名、身份与大小限制",Content=panel});
        Button(_form,"插入当前签名","compose-signature",async()=>{
            await Flush();if(_draft is not {} d)return;
            var text=_editor?.Signature?.Invoke(d.Account) ?? _page.Drafts.Profile(d.Account).Signature;
            if(string.IsNullOrWhiteSpace(text))throw new InvalidDataException("此账户还没有签名。");
            Append(d,text,"<p>"+WebUtility.HtmlEncode(text).Replace("\n","<br>")+"</p>");});
    }
    private void LoadProfile(MailAccount a)
    {
        var p=_page.Drafts.Profile(a.Id);_signature.Text=p.Signature;_aliases.Text=string.Join(", ",p.Aliases);_limit.Value=p.MaxBytes;
        _from.ItemsSource=p.Aliases.Prepend(a.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private void Append(MailDraft d,string text,string html)
    {
        string plain=d.Body.Plain+"\n"+text;var body=d.Body.Rich?new DraftBody(plain,d.Body.Html+html,plain,true):d.Body with{Plain=plain};
        Persist(d with{Body=body});Load(_draft!);
    }
    private void AddFiles()
    {
        _files.ItemTemplate=new FuncDataTemplate<ComposerPart>((f,_)=>new TextBlock{Text=f is null?"":$"{f.Name} · {f.Size:N0} 字节"+(f.Cid is null?"":" · 内联图片")});
        Field("附件（可拖入文件）",_files,"compose-files");
        DragDrop.SetAllowDrop(_files,true);_files.AddHandler(DragDrop.DropEvent,async(_,e)=>{
            var files=e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().ToArray();if(files is not null)await Guard(()=>ImportFiles(files,false));
        });
        var tools=new WrapPanel();Button(tools,"添加附件","compose-add",()=>Pick(false));Button(tools,"插入图片","compose-image",()=>Pick(true));
        Button(tools,"粘贴剪贴板图片","compose-paste",async()=>{
            var clipboard=TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidDataException("剪贴板不可用。");
            using var bitmap=await clipboard.TryGetBitmapAsync() ?? throw new InvalidDataException("剪贴板中没有图片。");
            using var stream=new MemoryStream();bitmap.Save(stream,Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);stream.Position=0;await Flush();await AddStream("剪贴板图片.png",stream,true,_stop.Token);
        });
        Button(tools,"移除所选附件","compose-remove",RemoveSelected);
        var cancel=new Button{Content="取消添加"};cancel.Click+=(_,_)=>_adding?.Cancel();tools.Children.Add(cancel);_form.Children.Add(tools);
        Button(_form,"预览发送内容","compose-preview",async()=>{
            await Flush();if(_draft is {} d)_status.Text=$"发件：{d.Identity}\nTo：{d.To}\nCc：{d.Cc}\nBcc（仅信封）：{d.Bcc}\n主题：{d.Subject}\n{d.Body.Plain}\n"+
                string.Join("、",d.Parts.Select(f=>f.Name))+$"\n账户上限 {_page.Drafts.Profile(d.Account).MaxBytes:N0} 字节。";
        });
    }
    private async Task Pick(bool inline)
    {
        var top=TopLevel.GetTopLevel(this);if(top is null)return;
        var chosen=await top.StorageProvider.OpenFilePickerAsync(new(){AllowMultiple=true,Title=inline?"选择内联图片":"选择附件"});
        await ImportFiles(chosen,inline);
    }
    private async Task ImportFiles(IEnumerable<IStorageFile> files,bool inline)
    {
        await Flush();using var adding=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);_adding=adding;
        try{foreach(var f in files){await using var input=await f.OpenReadAsync();await AddStream(f.Name,input,inline,adding.Token);}}
        finally{_adding=null;}
    }
    private async Task AddStream(string name,Stream stream,bool inline,CancellationToken token)
    {
        var d=_draft ?? throw new InvalidDataException("请先创建草稿。");
        if(inline && _editor?.SupportsRich==false)throw new InvalidDataException("请先选择支持内联图片的富文本编辑器。");
        if(inline && !d.Body.Rich && d.Body.Html.Length>0 && d.Body.RichText!=d.Body.Plain)throw new InvalidDataException("请先确定要保留的正文格式，再插入图片。");
        var part=await _page.Drafts.AddPart(d.Id,d.Account,name,stream,inline,token);
        if(d.Parts.Any(p=>p.Id==part.Id)){_status.Text="此文件已经在草稿中。";return;}
        var body=d.Body;
        if(inline)
        {
            string html=body.Rich?body.Html:"<div>"+WebUtility.HtmlEncode(body.Plain).Replace("\n","<br>")+"</div>";
            html+="<img src=\"cid:"+part.Cid+"\" alt=\""+WebUtility.HtmlEncode(name)+"\">";
            body=new(body.Plain,html,body.Plain,true);
        }
        Persist(d with{Parts=[..d.Parts,part],Body=body});Load(_draft!);
    }
    private async Task RemoveSelected()
    {
        await Flush();if(_draft is not {} d || _files.SelectedItem is not ComposerPart f)return;
        var body=d.Body;
        if(f.Cid is not null)
        {
            using var doc=new AngleSharp.Html.Parser.HtmlParser().ParseDocument(body.Html);
            foreach(var e in doc.QuerySelectorAll("img").Where(e=>e.GetAttribute("src")=="cid:"+f.Cid))e.Remove();
            body=body with{Html=doc.Body!.InnerHtml};
        }
        Persist(d with{Parts=d.Parts.Where(p=>p.Id!=f.Id).ToArray(),Body=body});Load(_draft!);
    }
}
