namespace MyMailBox.Builtins;

public static class PluginPermissionName
{
    public static string Display(string grant) => grant switch
    {
        "mail.read"=>"读取邮件", "mail.write"=>"修改邮件", "mail.send"=>"发送邮件", "mail.compose"=>"编辑邮件",
        "mail.storage"=>"管理邮件存储", "files.read"=>"读取文件", "files.write"=>"写入文件", "network.http"=>"访问网络",
        "ui.native"=>"显示自定义界面", "contacts.suggest"=>"提供地址建议", "notification.show"=>"显示通知",
        "automation.condition"=>"为自动任务提供判断条件", "automation.action"=>"为自动任务提供操作",
        "automation.schedule"=>"为自动任务提供时间安排", "updates.check"=>"检查软件更新", _ => grant
    };
}
