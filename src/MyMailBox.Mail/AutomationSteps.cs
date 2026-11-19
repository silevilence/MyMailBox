using System.Security.Cryptography;
using System.Text.Json;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class MailAutomationService
{
    private static bool Destructive(TaskAction a)=>a.Kind is TaskActionKind.Trash or TaskActionKind.DeletePermanently;
    private static bool SendsMail(TaskAction a) => a.Kind is TaskActionKind.AutoReply or TaskActionKind.AutoForward;
    private static string Approval(AutomationPlan plan)
    {
        var definition = new { plan.Id, plan.When, plan.Target, plan.Archive, plan.Actions, plan.Triggers, plan.PluginCondition, plan.PluginSchedule };
        var bytes = plan.Actions.Any(SendsMail)
            ? JsonSerializer.SerializeToUtf8Bytes(new { definition, plan.Enabled })
            : JsonSerializer.SerializeToUtf8Bytes(definition);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    public async Task<ActionPreview> PreviewActions(AutomationPlan plan,CancellationToken token=default)
    {
        await ValidateActions(plan,token,false);
        var targets=await Select(plan,token:token);var folders=(await Library(token)).Scopes;
        var effects=plan.Actions.Select(a=>a.Kind switch
        {
            TaskActionKind.AutoReply or TaskActionKind.AutoForward => ProtocolMailService.DescribeAutomatic(a.Kind == TaskActionKind.AutoForward, a.Argument),
            TaskActionKind.Extension=>"扩展动作："+Extensions.First(d=>d.Id==a.Extension!.Id).Name,TaskActionKind.Copy=>"复制完整邮件 → "+folders.First(s=>s.Folder==a.Destination).Name,
            TaskActionKind.SaveAttachments=>"保存附件 → "+a.Argument,TaskActionKind.ExportEml=>"导出完整 EML → "+a.Argument,
            TaskActionKind.Notify=>"显示通知："+a.Argument,TaskActionKind.Label=>"添加分类："+a.Argument,
            TaskActionKind.Trash=>"移至垃圾箱，邮件将离开原目录。",TaskActionKind.DeletePermanently=>"永久删除原文及附件，此操作无法撤销。",
            TaskActionKind.Read=>"标为已读",TaskActionKind.Unread=>"标为未读",TaskActionKind.Star=>"添加星标",_=>"移除星标"
        }).ToArray();
        for(int i=0;i<plan.Actions.Length;i++)if(plan.Actions[i] is {Kind:TaskActionKind.Extension,Extension:{} ext})
            effects[i]+=" · "+(await EvaluatePart(ext,AutomationPart.Action,plan.Target?.Account,targets.At,[],true,token)).Detail;
        return new(effects,targets.Items.Count,targets.SelectedBytes,Approval(plan));
    }
    private async Task ValidateActions(AutomationPlan p,CancellationToken token,bool save=true)
    {
        if(p.Actions.Length==0)return;
        if(p.Target?.Account is not {} source)throw new InvalidDataException("组合动作需要明确的来源账户。");
        var accounts=await new AccountService(_store).List(token);var account=accounts.FirstOrDefault(a=>a.Enabled&&a.Id==source);
        if(account is null || account.Provider is not ("local" or "imap" or "imap-smtp" or "pop3"))throw new InvalidDataException("此账户未提供任务动作能力。");
        var scopes=(await Library(token)).Scopes;
        for(int i=0;i<p.Actions.Length;i++)
        {
            var a=p.Actions[i];
            if(!Enum.IsDefined(a.Kind)||!Enum.IsDefined(a.Failure)||a.Retries is <0 or >3||a.Failure==StepFailure.Retry && a.Retries==0)
                throw new InvalidDataException("步骤失败时可停止、继续或重试 1–3 次。");
            if(a.Kind==TaskActionKind.Copy)
            {
                var dest=scopes.SingleOrDefault(s=>s.Folder.HasValue&&s.Folder==a.Destination);
                if(dest is null||dest.Folder==p.Target.Folder||dest.Account!=source && !accounts.Any(x=>x.Id==dest.Account&&x.Enabled&&x.Provider=="local"))
                    throw new InvalidDataException("请选择同账户其他目录或可验证的本地复制目标。");
            }
            if(a.Kind is TaskActionKind.SaveAttachments or TaskActionKind.ExportEml)
            {
                if(account.Provider is not ("local" or "pop3"))throw new InvalidDataException("自动导出需要本地完整原文，请先归档远端邮件。");
                if(string.IsNullOrWhiteSpace(a.Argument)||!Path.IsPathFullyQualified(a.Argument)||!Directory.Exists(a.Argument))throw new InvalidDataException("请选择已有的导出目录。");
            }
            if(a.Kind is TaskActionKind.Notify or TaskActionKind.Label && string.IsNullOrWhiteSpace(a.Argument))throw new InvalidDataException("请输入通知或分类文本。");
            if (SendsMail(a))
            {
                await _protocols.ValidateAutomatic(source, a.Kind == TaskActionKind.AutoForward, a.Argument, token);
                if (save && p.Enabled && p.Approval != Approval(p))
                    throw new InvalidDataException("保存自动外发前请预览收件人和范围，并明确确认。");
            }
            if(Destructive(a))
            {
                if(account.Provider is not ("local" or "pop3"))throw new InvalidDataException("此提供者未声明可自动执行的删除能力。");
                if(i!=p.Actions.Length-1||p.Archive is not null)throw new InvalidDataException("移到垃圾箱或永久删除必须是最后一步。");
                if(save && p.Approval!=Approval(p))throw new InvalidDataException("保存删除动作前请预览本次影响并确认。");
            }
        }
    }
    public Task<IReadOnlyList<TaskStepLog>> Steps(Guid run,CancellationToken token=default)=>Task.Run<IReadOnlyList<TaskStepLog>>(()=>
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.CommandText="SELECT item,step,kind,state,attempts,detail FROM automation_step WHERE run=$id ORDER BY item,step";
        c.Parameters.AddWithValue("$id",run.ToString());using var r=c.ExecuteReader();var rows=new List<TaskStepLog>();
        while(r.Read())rows.Add(new(r.GetInt32(0),r.GetInt32(1),(TaskActionKind)r.GetInt32(2),r.GetString(3),r.GetInt32(4),r.GetString(5)));return rows;
    },token);
    private async Task<(string,string)> ApplySteps(Guid run,int item,AutomationPlan p,TargetSnapshot snapshot,TargetMail mail,CancellationToken token)
    {
        using var db=_store.Open();using var c=db.CreateCommand();c.Parameters.AddWithValue("$id",run.ToString());c.Parameters.AddWithValue("$item",item);
        c.CommandText="SELECT count(*) FROM automation_step WHERE run=$id AND item=$item";
        if((long)c.ExecuteScalar()! ==0 && await Recheck(p,snapshot,mail,token) is {} stale)return("skipped",stale);
        bool partial=false;
        for(int i=0;i<p.Actions.Length;i++)
        {
            token.ThrowIfCancellationRequested();
            if(Run(run).State!="running")return("pending","已停止后续步骤。");
            var a=p.Actions[i];using var step=db.CreateCommand();
            step.Parameters.AddWithValue("$id",run.ToString());step.Parameters.AddWithValue("$item",item);step.Parameters.AddWithValue("$step",i);
            const string key=" WHERE run=$id AND item=$item AND step=$step";
            step.CommandText="INSERT OR IGNORE INTO automation_step VALUES($id,$item,$step,$op,$kind,'ready',0,'待执行')";
            step.Parameters.AddWithValue("$op",Guid.NewGuid().ToString());step.Parameters.AddWithValue("$kind",(int)a.Kind);step.ExecuteNonQuery();
            step.CommandText="SELECT op,state,detail FROM automation_step"+key;
            Guid op;string state,note;using(var r=step.ExecuteReader()){r.Read();op=Guid.Parse(r.GetString(0));state=r.GetString(1);note=r.GetString(2);}
            if(state is "done" or "skipped")continue;
            if(state=="continued"){partial=true;continue;}
            bool safeReplay=SendsMail(a) || a.Kind is TaskActionKind.Extension or TaskActionKind.SaveAttachments or TaskActionKind.Read or TaskActionKind.Unread or TaskActionKind.Star or TaskActionKind.Unstar or TaskActionKind.Label or TaskActionKind.ExportEml;
            if(state=="uncertain"||state=="started"&&!safeReplay)
            {
                Set("uncertain","上次步骤回执缺失，已停止。请核对副本、导出物或通知记录。");throw new InvalidDataException("存在未确认的外部效果，请核对步骤记录。");
            }
            if((Destructive(a) || SendsMail(a)) && await RecheckCurrent(p,snapshot,mail,i>0,token) is {} changed)
            {Set("skipped",changed);return("skipped",changed);}
            int limit=a.Failure==StepFailure.Retry?1+a.Retries:1;
            for(int attempt=1;attempt<=limit;attempt++)
            {
                step.CommandText="UPDATE automation_step SET state='started',attempts=attempts+1"+key;step.ExecuteNonQuery();
                try
                {
                    if (SendsMail(a))
                    {
                        var result = await AutomaticStep(run,a,mail,op,token);
                        Set(result.Skipped ? "skipped" : "done", result.Detail);
                    }
                    else { note=await ApplyAction(run,a,mail,op,token);Set("done",note); }
                    break;
                }
                catch(ExtensionPaused){Set("ready","等待原扩展和权限恢复。");throw;}
                catch(OperationCanceledException){throw;}
                catch(Exception e)
                {
                    // An interrupted or negative remote COPY response can leave an unacknowledged side effect.
                    if(a.Kind==TaskActionKind.Copy && mail.Validity.HasValue){Set("uncertain","远端复制结果未确认。");throw;}
                    if(attempt<limit && (safeReplay||mail.Validity is null && a.Kind is TaskActionKind.Copy or TaskActionKind.Trash or TaskActionKind.DeletePermanently)){Set("retry",e.Message);continue;}
                    if(a.Failure==StepFailure.Continue){Set("continued",e.Message);partial=true;break;}
                    Set("failed",e.Message);throw;
                }
            }
            void Set(string next,string detail)
            {
                if(step.Parameters.Contains("$newState"))step.Parameters.RemoveAt("$newState");if(step.Parameters.Contains("$detail"))step.Parameters.RemoveAt("$detail");
                step.Parameters.AddWithValue("$newState",next);step.Parameters.AddWithValue("$detail",detail);
                step.CommandText="UPDATE automation_step SET state=$newState,detail=$detail"+key;step.ExecuteNonQuery();
            }
        }
        return partial?("partial","部分步骤失败，已按策略继续。"):("done","已完成所有步骤。");
    }
    private async Task<AutomaticMailResult> AutomaticStep(Guid run,TaskAction a,TargetMail mail,Guid operation,CancellationToken token)
    {
        using var db = _store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT r.definition,p.definition FROM automation_run r JOIN automation_plan p ON p.id=r.plan WHERE r.id=$id";
        cmd.Parameters.AddWithValue("$id",run.ToString());
        using (var row = cmd.ExecuteReader())
        {
            if (!row.Read()) throw new InvalidDataException("自动外发任务已移除。");
            var frozen = JsonSerializer.Deserialize<AutomationPlan>(row.GetString(0))!;
            var current = JsonSerializer.Deserialize<AutomationPlan>(row.GetString(1))!;
            if (!current.Enabled || frozen.Approval != Approval(frozen) || current.Approval != frozen.Approval)
                throw new InvalidDataException("自动外发批准已变更，请重新预览任务。");
        }
        return await _protocols.ApplyAutomatic(operation,mail.Account,mail.Folder,mail.Key,a.Kind==TaskActionKind.AutoForward,a.Argument,token);
    }

    private async Task<string> ApplyAction(Guid run,TaskAction a,TargetMail mail,Guid operation,CancellationToken token)
    {
        if(a.Kind==TaskActionKind.Extension)
        {
            var response=await EvaluatePart(a.Extension!,AutomationPart.Action,mail.Account,_clock.GetUtcNow(),[],false,token);
            if(response.Effect is not {} effect || effect.Kind is not (TaskActionKind.Read or TaskActionKind.Unread or TaskActionKind.Star or TaskActionKind.Unstar or TaskActionKind.Label))
                throw new InvalidDataException("动作扩展需要返回宿主允许的幂等标记或分类操作。");
            return await ApplyAction(run,effect,mail,operation,token);
        }
        if(a.Kind==TaskActionKind.Notify && _activityNotices){using var db=_store.Open();using var c=db.CreateCommand();ActivityCenter.Add(c,"notice/"+operation,"task",mail.Account.ToString(),a.Argument,"由邮件任务生成。","task",run.ToString());return "已提交到活动中心。";}
        if(a.Kind==TaskActionKind.Notify){if(_notify is null)throw new InvalidOperationException("当前宿主无法显示通知。");_notify(a.Argument);return "已提交通知。";}
        if(a.Kind is TaskActionKind.ExportEml or TaskActionKind.SaveAttachments)
        {
            var id=Guid.Parse(mail.Key);IMessageExporter exporter=a.Kind==TaskActionKind.ExportEml?new EmlExporter(_store):
                new AttachmentExporter(_store,new MimeReader(),new AttachmentService(_store));
            string destination=a.Argument;
            string? published=null;
            if(a.Kind==TaskActionKind.SaveAttachments)
            {
                published=Path.Combine(a.Argument,"附件-"+operation.ToString("N"));
                if(Directory.Exists(published))return "已核实此步骤的附件目录。";
                destination=published+".partial-"+Guid.NewGuid().ToString("N");
            }
            var report=await exporter.Export(new([new(id,mail.Subject)],destination),cancellation:token);
            token.ThrowIfCancellationRequested();var error=report.Items.FirstOrDefault(r=>r.Status=="failed");if(error is not null)throw new IOException(error.Detail);
            if (published is not null)
            {
                // Windows may hold a just-exported attachment open briefly. Retry only the final rename.
                for (int attempt = 0; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try { Directory.Move(destination, published); break; }
                    catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 3 &&
                        ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xffff) is 5 or 32 or 33)
                    {
                        await Task.Delay(100 * (attempt + 1), token);
                    }
                }
            }
            return string.Join("；",report.Items.Select(r=>r.Detail));
        }
        if(a.Kind is TaskActionKind.Copy or TaskActionKind.Trash or TaskActionKind.DeletePermanently)
        {
            if(a.Kind==TaskActionKind.Copy && (await _store.Folders(token)).Any(f=>f.Id==a.Destination && f.AccountId!=mail.Account))
            {
                await _protocols.Plan(new(operation,mail.Account,mail.Folder,mail.Key,a.Destination!.Value,false),token);
                var result=await _protocols.Continue(operation,token:token);if(result.Phase!="done")throw new InvalidDataException(result.Detail);return result.Detail;
            }
            var kind=a.Kind switch{TaskActionKind.Copy=>MailAction.Copy,TaskActionKind.Trash=>MailAction.Trash,_=>MailAction.DeletePermanently};
            var row=(await _protocols.Apply(new MailActionRequest(mail.Account,mail.Folder,[mail.Key],kind,a.Destination){OperationId=operation},token)).Single();
            if(!row.Success)throw new InvalidDataException(row.Detail);return row.Detail;
        }
        var mark=a.Kind switch{TaskActionKind.Read=>MailMark.Read,TaskActionKind.Unread=>MailMark.Unread,TaskActionKind.Star=>MailMark.Star,
            TaskActionKind.Unstar=>MailMark.Unstar,TaskActionKind.Label=>MailMark.AddLabel,_=>throw new InvalidDataException("不支持的任务动作。")};
        var outcome=(await _protocols.Mark(mail.Account,mail.Folder,[mail.Key],mark,a.Argument,token)).Single();
        if(!outcome.Success)throw new InvalidDataException(outcome.Detail);return outcome.Detail;
    }
}
