using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed partial class ReceiveRuleService
{
    private FileStream? AcquireProcessor()
    {
        try { return new(store.DatabasePath + ".rules.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }

    private static bool NeedsBody(ConditionNode n) => n.Criterion?.Field is "body" or "header" or "attachment" or "priority" || n.Children.Any(NeedsBody);
    private const string Missing = "EXISTS(SELECT 1 FROM imap_summaries WHERE folder=$f AND uid=$u AND message IS NULL AND NOT missing)";
    private bool MissingBody(Work work)
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT "+Missing;
        c.Parameters.AddWithValue("$f",work.Folder.ToString());c.Parameters.AddWithValue("$u",work.Item);return (long)c.ExecuteScalar()! != 0;
    }
    private void WaitForBody(Work work)
    {
        using var db=store.Open();using var c=db.CreateCommand();
        c.CommandText=$"UPDATE rule_run SET state=CASE WHEN {Missing} THEN 'waiting' ELSE 'pending' END,detail='等待正文阶段' WHERE id=$id";
        c.Parameters.AddWithValue("$f",work.Folder.ToString());c.Parameters.AddWithValue("$u",work.Item);c.Parameters.AddWithValue("$id",work.Id);c.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<RuleActionLog>> Actions(string run,CancellationToken token=default) => Task.Run<IReadOnlyList<RuleActionLog>>(() =>
    {
        using var db=store.Open();using var c=db.CreateCommand();c.CommandText="SELECT op,ordinal,kind,state,source,item,destination,target,undo,detail FROM rule_action_log WHERE run=$run ORDER BY ordinal";
        c.Parameters.AddWithValue("$run",run);using var r=c.ExecuteReader();var rows=new List<RuleActionLog>();
        while(r.Read())rows.Add(new(Guid.Parse(r.GetString(0)),r.GetInt32(1),(RuleActionKind)r.GetInt32(2),r.GetString(3),Guid.Parse(r.GetString(4)),
            r.GetString(5),r.IsDBNull(6)?null:Guid.Parse(r.GetString(6)),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:Guid.Parse(r.GetString(8)),r.GetString(9)));
        return rows;
    },token);

    private RuleActionLog StartAction(Work work,int ordinal,RuleAction action,Guid folder,string key)
    {
        var op=Guid.NewGuid();using var db=store.Open();using var c=db.CreateCommand();
        c.CommandText="INSERT INTO rule_action_log VALUES($op,$run,$n,$kind,'started',$f,$key,$dest,NULL,NULL,'已记录调用意图')";
        c.Parameters.AddWithValue("$op",op.ToString());c.Parameters.AddWithValue("$run",work.Id);c.Parameters.AddWithValue("$n",ordinal);
        c.Parameters.AddWithValue("$kind",(int)action.Kind);c.Parameters.AddWithValue("$f",folder.ToString());c.Parameters.AddWithValue("$key",key);
        c.Parameters.AddWithValue("$dest",(object?)action.Destination?.ToString() ?? DBNull.Value);c.ExecuteNonQuery();
        return new(op,ordinal,action.Kind,"started",folder,key,action.Destination,null,null,"");
    }

    private void Record(RuleActionLog row,string state,string detail,string? target=null,Guid? undo=null)
    {
        using var db=store.Open();using var c=db.CreateCommand();
        c.CommandText="UPDATE rule_action_log SET state=$state,detail=$detail,target=$target,undo=$undo WHERE op=$op";
        c.Parameters.AddWithValue("$state",state);c.Parameters.AddWithValue("$detail",detail);c.Parameters.AddWithValue("$target",(object?)target ?? DBNull.Value);
        c.Parameters.AddWithValue("$undo",(object?)undo?.ToString() ?? DBNull.Value);c.Parameters.AddWithValue("$op",row.Operation.ToString());c.ExecuteNonQuery();
    }

    private bool LocalMoveConfirmed(RuleActionLog action)
    {
        using var db=store.Open();using var c=db.CreateCommand();
        c.CommandText="SELECT count(*) FROM mail_move_undo u JOIN messages m ON m.id=u.message AND m.folder_id=u.destination WHERE u.id=$op AND u.source=$src AND u.destination=$dest AND u.message=$key";
        c.Parameters.AddWithValue("$op",action.Operation.ToString());c.Parameters.AddWithValue("$src",action.Source.ToString());
        c.Parameters.AddWithValue("$dest",(object?)action.Destination?.ToString() ?? DBNull.Value);c.Parameters.AddWithValue("$key",action.Key);return (long)c.ExecuteScalar()! == 1;
    }

    private async Task ApplyActions(Work work,IReadOnlyList<RuleActionLog> journal,CancellationToken token)
    {
        Guid folder=work.Folder; string key=work.Item; var notes=new List<string>();
        for(int i=0;i<work.Rule.Actions.Length;i++)
        {
            token.ThrowIfCancellationRequested();
            var action=work.Rule.Actions[i]; var prior=journal.FirstOrDefault(row=>row.Ordinal==i);
            if(prior is not null)
            {
                if(prior.State=="skipped"){notes.Add(prior.Detail);continue;}
                if(prior.State!="done")
                {
                    if(prior.Kind!=RuleActionKind.Move || !LocalMoveConfirmed(prior))
                    {
                        Record(prior,"uncertain","上次调用结果未确认，已停止以免重复执行。");
                        Finish(work,"uncertain",$"操作 {prior.Operation} 的结果需要核对。",true);return;
                    }
                    Record(prior,"done","已按操作 ID 核对本地移动。",prior.Key,prior.Operation);
                }
                if(prior.Kind==RuleActionKind.Move){folder=prior.Destination!.Value;key=prior.Target ?? prior.Key;}
                notes.Add("已核对操作 "+prior.Operation);continue;
            }
            var record=StartAction(work,i,action,folder,key);
            try
            {
                if (SendsMail(action))
                {
                    using var db = store.Open();using var check = db.CreateCommand();
                    check.CommandText = "SELECT definition FROM receive_rule WHERE id=$id AND enabled=1";
                    check.Parameters.AddWithValue("$id",work.Rule.Id.ToString());
                    var active = check.ExecuteScalar() is string json ? System.Text.Json.JsonSerializer.Deserialize<ReceiveRule>(json) : null;
                    if (active is null || work.Rule.MailApproval != Approval(work.Rule) || active.MailApproval != work.Rule.MailApproval)
                        throw new InvalidDataException("规则的外发批准已变更或停用。");
                    var detail = await Outbound.ApplyAutomatic(record.Operation,work.Account,folder,key,action.Kind==RuleActionKind.AutoForward,action.MailArgument??"",token);
                    Record(record,detail.Skipped ? "skipped" : "done",detail.Detail,key);notes.Add(detail.Detail);
                }
                else if(action.Kind==RuleActionKind.Move)
                {
                    var row=(await mailActions.Apply(new MailActionRequest(work.Account,folder,[key],MailAction.Move,action.Destination){OperationId=record.Operation},token)).Single();
                    if(!row.Success)throw new InvalidDataException(row.Detail);
                    folder=action.Destination!.Value;key=row.DestinationKey ?? key;Record(record,"done",row.Detail,key,row.UndoId);notes.Add(row.Detail);
                }
                else
                {
                    var mark=action.Kind switch{RuleActionKind.Read=>MailMark.Read,RuleActionKind.Unread=>MailMark.Unread,RuleActionKind.Star=>MailMark.Star,
                        RuleActionKind.Unstar=>MailMark.Unstar,RuleActionKind.Label=>MailMark.AddLabel,_=>throw new InvalidDataException("未知规则动作。")};
                    var row=(await organizer.Mark(work.Account,folder,[key],mark,action.Label ?? "",token)).Single();
                    if(!row.Success)throw new InvalidDataException(row.Detail);
                    Record(record,"done",row.Detail,key);notes.Add(row.Detail);
                }
            }
            catch(Exception e) when(e is not OperationCanceledException) {Record(record,"unconfirmed",e.Message);throw;}
        }
        Finish(work,"applied","已命中："+string.Join("；",notes),work.Rule.Stop);
    }
}
