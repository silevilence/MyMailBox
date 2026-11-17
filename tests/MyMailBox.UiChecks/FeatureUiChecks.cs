using System.Net;
using System.Net.Sockets;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using MimeKit;
using MyMailBox.Mail;

/// <summary>One consolidated desktop run, synthetic mail only, no protocol credentials.</summary>
internal sealed class FeatureUiChecks : IDisposable
{
    private readonly string _workspace, _output, _eml;
    private readonly TcpListener _tracker = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private int _requests;

    public FeatureUiChecks(string workspace, string output)
    {
        _workspace = workspace; _output = output;
        _tracker.Start(); var port = ((IPEndPoint)_tracker.LocalEndpoint).Port;
        _server = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _tracker.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _requests);
                    await client.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        _eml = Path.Combine(output, "验收样本.eml");
        var message = new MimeMessage { Subject = "集中验收：中文长邮件", Date = DateTimeOffset.Now.AddDays(1) };
        message.From.Add(new MailboxAddress("验收发件人", "sender@example.test")); message.To.Add(MailboxAddress.Parse("reader@example.test"));
        var builder = new BodyBuilder { TextBody = "中文纯文本正文。原始邮件与附件应保持完整。" };
        builder.HtmlBody = "<h1>中文阅读与打印验收</h1><p>这是第一段中文正文。</p><img src='cid:logo' width='24' height='24'><table><tr><th>项目</th><th>状态</th></tr><tr><td>中文邮件</td><td>保留原文</td></tr></table>"
            + string.Concat(Enumerable.Range(1, 100).Select(i => $"<p>第 {i} 段：用于核验中文字体、跨页排版和内容完整性。邮件阅读副本不能替代完整原文备份。</p>"))
            + $"<script>fetch('http://127.0.0.1:{port}/script')</script><iframe src='http://127.0.0.1:{port}/frame'></iframe><img src='http://127.0.0.1:{port}/tracking'><style>body{{background:url(http://127.0.0.1:{port}/css)}}</style><p>全文结束标记。</p>";
        var logo = builder.LinkedResources.Add("logo.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII="), new ContentType("image", "png")); logo.ContentId = "logo";
        builder.Attachments.Add("附件说明.txt", Encoding.UTF8.GetBytes("附件中文内容，不应执行任何代码。"), new ContentType("text", "plain"));
        message.Body = builder.ToMessageBody(); message.WriteTo(_eml);
        var store = new SqliteMailStore(Path.Combine(workspace, "mail")); var parser = new MyMailBox.Mail.MimeReader();
        for (var i = 0; i < 53; i++)
        {
            var raw = Encoding.UTF8.GetBytes($"From: sender@example.test\r\nTo: reader@example.test\r\nSubject: Paging {i:D2}\r\nDate: Mon, 16 Nov 2026 00:00:00 +0000\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n分页邮件 {i}");
            store.Import(SqliteMailStore.Inbox, raw, parser.Parse(raw).GetAwaiter().GetResult()).GetAwaiter().GetResult();
        }
    }

    public void Run(Ui ui, Window window, bool readingOnly = false)
    {
        ui.Click("nav-local-mail/inbox"); ui.Text("mail-file", _eml); ui.Click("mail-import");
        ui.ListContains("mail-list", "集中验收"); ui.SelectList("mail-list", 0); ui.Body("中文纯文本正文");
        ui.Click("mail-next"); ui.ListContains("mail-list", "Paging"); ui.Click("mail-previous"); ui.SelectList("mail-list", 0); ui.Body("中文纯文本正文");
        ui.Select("mail-mode", 1); ui.Body("CID:logo"); ui.Select("mail-mode", 2); ui.Body("MIME-Version"); ui.Select("mail-mode", 0);
        ui.Select("mail-encoding", 1); ui.Body("中文纯文本正文"); ui.Select("mail-encoding", 0);
        ui.Select("mail-mode", 3); ui.Status("mail-status", "HTML 已加载");
        Thread.Sleep(400); // Navigation completion precedes WebView's first compositor frame.
        NativeWindowEvidence.Write(window, Path.Combine(_output, "native-windows.txt"));
        if (ui.Find("mail-status").Name.Contains("不可用")) throw new Exception("WebView2 HTML unavailable");
        Capture.Element(window).ToFile(Path.Combine(_output, "10-html.png"));
        if (ui.Find("mail-list").BoundingRectangle.Height < window.BoundingRectangle.Height * 0.25) throw new Exception("Mail tools leave too little reading space");
        ui.Click("mail-zoom-in"); ui.Click("mail-zoom-out");
        if (Volatile.Read(ref _requests) != 0) throw new Exception("Mail content contacted remote tracker");
        ui.Expand("远程多媒体"); ui.Click("mail-remote-preview-action"); ui.Status("mail-remote-preview", "默认阻止");
        ui.Click("mail-remote-allow"); ui.Status("mail-remote-preview", "本封"); ui.Click("mail-remote-revoke"); ui.Status("mail-remote-preview", "默认阻止"); ui.Expand("远程多媒体", false);
        if (Volatile.Read(ref _requests) != 0) throw new Exception("Private network resource bypassed policy");
        ui.Select("mail-mode", 0); ui.Expand("附件"); ui.Select("mail-attachments", 1);
        ui.Click("mail-attachment-preview"); ui.Body("附件中文内容");
        var attachmentDirectory = Path.Combine(_output, "saved-attachments"); ui.Text("mail-attachment-directory", attachmentDirectory); ui.Click("mail-attachment-save");
        WaitFile(attachmentDirectory, "*.txt"); ui.Expand("附件", false);
        ui.Expand("导出邮件"); var exportDirectory = Path.Combine(_output, "reading-copies"); ui.Text("mail-export-directory", exportDirectory);
        foreach (var item in new[] { (0, "*.eml"), (4, "*.pdf"), (5, "*.html"), (6, "*.txt") })
        {
            ui.Select("mail-export-format", item.Item1); ui.Click("mail-export"); WaitFile(exportDirectory, item.Item2);
        }
        ui.Expand("导出邮件", false);
        if (!File.ReadAllBytes(Directory.GetFiles(exportDirectory, "*.eml").Single()).SequenceEqual(File.ReadAllBytes(_eml))) throw new Exception("UI EML export changed raw bytes");
        Console.WriteLine("PASS UI local MIME import/read/paging/encoding, HTML isolation, attachment preview/save, EML/PDF/HTML/TXT exports");
        if (readingOnly) return;

        ui.Click("nav-accounts/manage"); ui.Click("account-new"); ui.Text("account-name", "验收本地账户"); ui.Text("account-alias", "ui-local"); ui.Text("account-tags", "验收,归档"); ui.Click("account-save"); ui.Status("account-status", "已保存");
        ui.Text("account-name", "验收账户已改名"); ui.Click("account-save"); ui.Status("account-status", "已保存");
        var account = new AccountService(new SqliteMailStore(Path.Combine(_workspace, "mail"))).List().GetAwaiter().GetResult().Single(a => a.Alias == "ui-local");
        ui.Click("nav-mail-selectors/choose"); ui.Select("selection-kind", 1); ui.Text("selection-tag", "验收"); ui.Click("selection-refresh");
        ui.Check("choice-" + account.Id); ui.Click("selection-save"); ui.Status("selection-status", "固定选择已保存");
        ui.Click("selection-restore"); ui.Status("selection-status", "已恢复固定集合");
        ui.Check("selection-dynamic"); ui.Click("selection-save"); ui.Status("selection-status", "动态标签集合已保存");

        ui.Click("nav-local-mail/local-mailboxes"); ui.Text("local-name", "独立归档验收"); ui.Text("local-alias", "ui-archive"); ui.Text("local-tags", "验收"); ui.Click("local-create"); ui.Status("local-status", "已创建");
        ui.Text("local-folder-name", "中文目录"); ui.Click("local-folder-create"); ui.Status("local-status", "目录已创建");
        ui.Text("local-eml", _eml); ui.Click("local-import"); ui.Status("local-status", "已归档");
        ui.Click("local-close"); ui.Status("local-status", "已关闭"); ui.Click("local-open"); ui.Status("local-status", "已打开");
        Capture.Element(window).ToFile(Path.Combine(_output, "11-local-mailboxes.png"));
        Console.WriteLine("PASS UI account create/rename, stable and dynamic selections, local archive create/import/close/reopen");

        ui.Click("nav-local-mail/export"); ui.Check("export-scope-Account-" + SqliteMailStore.LocalAccount); ui.Text("export-subject", "集中验收"); ui.Click("export-preview"); ui.Status("export-status", "去重后 1 封");
        ui.Select("export-format", 2); ui.Text("export-directory", Path.Combine(_output, "bulk-json")); ui.Click("export-execute"); ui.Status("export-status", "1 成功，0 失败");
        Capture.Element(window).ToFile(Path.Combine(_output, "12-export.png"));
        ui.Click("nav-local-mail/archive"); ui.Text("archive-file", Path.Combine(_output, "mail-archive.zip")); ui.Click("archive-export"); ui.Result("已导出"); ui.Click("archive-import"); ui.Result("已导入");
        ui.Click("nav-local-mail/backup"); ui.Text("backup-file", Path.Combine(_output, "workspace-backup.zip")); ui.Click("backup-create"); ui.Result("已完成");
        var restored = Path.Combine(_output, "restored-" + Guid.NewGuid().ToString("N")); ui.Text("restore-directory", restored); ui.Click("backup-restore"); ui.Result("已恢复到");
        if (new SqliteMailStore(Path.Combine(restored, "mail")).List(SqliteMailStore.Inbox, 0, 100).GetAwaiter().GetResult().Items.Count != 54) throw new Exception("Restored mailbox differs");
        Console.WriteLine("PASS UI bulk export preview/results, archive conflict remapping and workspace backup/restore");

        ui.Click("nav-reminders/list"); ui.Text("reminder-title", "集中验收即时提醒"); ui.Text("reminder-time", DateTime.Now.AddSeconds(-2).ToString("yyyy-MM-dd HH:mm:ss")); ui.Click("reminder-create"); ui.Status("reminder-status", "已保存"); ui.Notice("集中验收即时提醒");
        ui.Click("reminder-snooze"); ui.Status("reminder-status", "已延后"); ui.Click("reminder-cancel"); ui.Status("reminder-status", "已取消"); ui.Click("global-notice-close");
        ui.Click("nav-reminders/recurring"); ui.Text("schedule-title", "验收循环计划"); ui.Select("schedule-kind", 8); ui.Text("schedule-cron", "invalid"); ui.Click("schedule-preview"); ui.Status("schedule-status", "操作未完成");
        ui.Text("schedule-cron", "0 9 * * MON-FRI"); ui.Click("schedule-preview"); ui.Status("schedule-preview-result", "09:00"); ui.Click("schedule-create"); ui.Status("schedule-status", "已保存");
        ui.SelectList("schedule-list", 0); ui.Click("schedule-pause"); ui.Status("schedule-status", "已暂停"); ui.Click("schedule-resume"); ui.Status("schedule-status", "已重新启用");
        Capture.Element(window).ToFile(Path.Combine(_output, "13-schedule.png"));
        ui.Click("nav-updates/settings"); ui.Status("update-install-status", "公钥"); ui.Text("update-interval", "0"); ui.Click("update-save"); ui.Status("update-status", "1");
        ui.Text("update-interval", "6"); ui.Check("update-background", false); ui.Click("update-save"); ui.Status("update-status", "已保存");
        Capture.Element(window).ToFile(Path.Combine(_output, "14-updates.png"));
        Console.WriteLine("PASS UI reminder delivery/snooze/cancel, CRON validation/preview/save/pause/resume, update settings and missing-key diagnosis");
        File.WriteAllText(Path.Combine(_output, "features-result.txt"), "PASS consolidated features\nRemote content requests: " + Volatile.Read(ref _requests) + "\nPDF: " + Directory.GetFiles(exportDirectory, "*.pdf").Single());
    }

    private static void WaitFile(string directory, string pattern)
    {
        var end = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < end)
        {
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, pattern).Any(p => new FileInfo(p).Length > 0)) return;
            Thread.Sleep(100);
        }
        throw new Exception("No completed output: " + Path.Combine(directory, pattern));
    }
    public void Dispose() { _stop.Cancel(); _tracker.Stop(); _server.GetAwaiter().GetResult(); _stop.Dispose(); }
}
