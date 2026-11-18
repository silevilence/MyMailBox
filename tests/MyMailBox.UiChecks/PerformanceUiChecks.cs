using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using System.Runtime.InteropServices;
using MyMailBox.Mail;
using System.Diagnostics;
using System.Text.Json;
using Application=FlaUI.Core.Application;

internal static class PerformanceUiChecks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(nint handle, out IoCounters counters);

    private sealed record Sample(string Operation,double Milliseconds);
    public static void Run(int size)
    {
        if(size is not (10000 or 100000))throw new ArgumentOutOfRangeException(nameof(size));
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var home=Path.Combine(root,"artifacts","performance-v1",size.ToString(),"ui");
        if(!File.Exists(Path.Combine(home,"mail","mail.db")))throw new FileNotFoundException("请先执行 perf-prepare-ui。");
        var store=new SqliteMailStore(Path.Combine(home,"mail"));var protocol=new ProtocolMailService(store);var auto=new MailAutomationService(store,new(store,protocol),protocol);
        var folder=new AvailableMailStore(store).Folders().Result.First(f=>f.Name=="第 1 层");
        var plan=auto.Plans().Result.Single(p=>p.Name=="测量期间归档");
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("MYMAILBOX_UI_EXE")??Path.Combine(root,"artifacts/publish/MyMailBox.Desktop.exe")){UseShellExecute=false};
        start.ArgumentList.Add("--measure-frames");start.Environment["MYMAILBOX_HOME"]=home;
        var samples=new List<Sample>();
        using var uia=new UIA3Automation();using var app=Application.Launch(start);
        var window=app.GetMainWindow(uia,TimeSpan.FromSeconds(20))??throw new Exception("未发现基准窗口");var ui=new Ui(window);using var process=Process.GetProcessById(app.ProcessId);
        void Measure(string op,Action action,Func<bool> ready)
        {
            var ts=Stopwatch.StartNew();action();
            if(!SpinWait.SpinUntil(ready,TimeSpan.FromSeconds(25)))throw new Exception("测量未完成："+op);
            samples.Add(new(op,ts.Elapsed.TotalMilliseconds));
        }
        try
        {
            ui.Find("nav-diagnostics");ui.Click("nav-local-mail/inbox");var box=ui.Find("mail-folders").AsComboBox();box.Expand();var choice=box.Items.FirstOrDefault(i=>i.Text==folder.Path)??throw new Exception($"找不到目录 {folder.Path}，界面可见：{string.Join(" | ",box.Items.Select(i=>i.Text))}");choice.Select();box.Collapse();
            ui.Status("mail-status","第 1–50");var run = auto.Runs(plan.Id).Result.FirstOrDefault(r => r.State is "ready" or "running") ??
                auto.Start(plan.Id,"manual/performance/"+Guid.NewGuid()).Result;
            int baseline = run.Done;
            if (!SpinWait.SpinUntil(() => auto.Runs(plan.Id).Result.Single(r => r.Id == run.Id).Done > baseline, 25000))
                throw new Exception("后台归档没有开始处理样本。");
            int atStart = auto.Runs(plan.Id).Result.Single(r => r.Id == run.Id).Done;
            if (!GetProcessIoCounters(process.Handle, out var ioStart)) throw new InvalidOperationException("无法读取进程 I/O。");
            var status=ui.Find("mail-status");var next=ui.Find("mail-next").AsButton();var previous=ui.Find("mail-previous").AsButton();
            var list=ui.Find("mail-list").AsListBox();var body=ui.Find("mail-body").AsTextBox();
            for(int i=0;i<10;i++)
            {
                bool forward=i%2==0;Measure("page",()=>{if(forward)next.Invoke();else previous.Invoke();},()=>status.Name.Contains(forward?"第 51–100":"第 1–50"));
                var item=list.Items[Math.Min(1,list.Items.Length-1)];
                Measure("read",()=>item.Select(),()=>body.Text.Contains("记录 "));
            }
            int realized = list.Items.Length;
            list.Items[0].Focus();
            var scrollStart = Stopwatch.GetTimestamp();
            for (int cycle = 0; cycle < 3; cycle++)
            {
                for (int step = 0; step < 6; step++) { Keyboard.Type(VirtualKeyShort.NEXT); Thread.Sleep(80); }
                for (int step = 0; step < 6; step++) { Keyboard.Type(VirtualKeyShort.PRIOR); Thread.Sleep(80); }
            }
            var scrollEnd = Stopwatch.GetTimestamp();
            ui.Click("nav-accounts/search");var value=ui.Find("search-value").AsTextBox();var search=ui.Find("search-run").AsButton();
            var cancel=ui.Find("search-cancel").AsButton();var feedback=ui.Find("search-status");
            for(int i=0;i<10;i++)
            {
                value.Text=i%2==0?"季度会议":"发票核对";
                Measure("search-first-page",()=>search.Invoke(),()=>feedback.Name.Contains("第 1 页"));
                Measure("cancel-feedback",()=>cancel.Invoke(),()=>feedback.Name.Contains("已取消"));
            }
            var completed=auto.Runs(plan.Id).Result.Single(r=>r.Id==run.Id);
            if(completed.Done <= atStart)throw new Exception("测量期间缺少后台归档进度。");
            if (!GetProcessIoCounters(process.Handle, out var ioEnd)) throw new InvalidOperationException("无法读取进程 I/O。");
            process.Refresh();long working=process.PeakWorkingSet64;window.Close();if(!process.WaitForExit(15000))throw new Exception("基准应用未退出。");
            var ticks = JsonSerializer.Deserialize<long[]>(File.ReadAllText(Path.Combine(home,"frame-ticks.json")))!;
            var frames = ticks.Zip(ticks.Skip(1), (a,b) => Stopwatch.GetElapsedTime(a,b).TotalMilliseconds).ToArray();
            var scrolling = frames.Where((_,i) => ticks[i] >= scrollStart && ticks[i + 1] <= scrollEnd).ToArray();
            static double P95(IEnumerable<double> values){var sorted=values.Order().ToArray();return sorted[(int)Math.Ceiling(.95*sorted.Length)-1];}
            var p95=samples.GroupBy(s=>s.Operation).ToDictionary(g=>g.Key,g=>Math.Round(P95(g.Select(v=>v.Milliseconds)),2));
            if (scrolling.Length == 0) throw new Exception("滚动期间没有记录到渲染帧。");
            var output = new { size, p95, peakWorkingSet = working,
                readBytes = ioEnd.ReadBytes - ioStart.ReadBytes, writeBytes = ioEnd.WriteBytes - ioStart.WriteBytes,
                archiveCompleted = completed.Done - atStart, realizedItems = realized, pageSize = 50, frames = frames.Length,
                frameP95 = P95(frames), scrollFrames = scrolling.Length, scrollP95 = P95(scrolling),
                scrollOver33Ms = scrolling.Count(ms => ms > 1000d/30), scrollOver50Ms = scrolling.Count(ms => ms > 50),
                scrollMax = scrolling.Max(), samples };
            var json=JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(home,"ui-measure.json"),json);
            Console.WriteLine(JsonSerializer.Serialize(new{size,p95,output.frameP95,output.archiveCompleted}));
            if(frames.Length==0||p95["page"]>300||p95["read"]>500||p95["cancel-feedback"]>=100||p95["search-first-page"]>(size==10000?1000:2000))
                throw new Exception("桌面性能超出目标，见 ui-measure.json。");
        }
        finally{if(!process.HasExited){try{window.Close();}catch{}if(!process.WaitForExit(8000))app.Kill();}}
    }
}
