using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace MyMailBox.Mail;

public sealed record OutlookRuleStore(string Profile, string Id, string Name)
{
    public override string ToString() => Name;
}

public static class ClassicOutlookRules
{
    public static Task<OutlookRuleStore[]> Stores(CancellationToken token) =>
        InOpenSession<OutlookRuleStore[]>(session =>
        {
            var stores = new List<OutlookRuleStore>();
            string profile = session.CurrentProfileName;
            foreach (var item in session.Stores)
            {
                token.ThrowIfCancellationRequested();
                stores.Add(new(profile, item.StoreID, item.DisplayName));
            }
            return stores.ToArray();
        }, token);

    public static Task<OutlookRuleInventory> Read(OutlookRuleStore store, CancellationToken token) =>
        InOpenSession<OutlookRuleInventory>(session =>
        {
            if ((string)session.CurrentProfileName != store.Profile)
                throw new InvalidDataException("Outlook 已切换邮箱配置，请重新选择邮箱。");
            dynamic mailbox = session.GetStoreFromID(store.Id);
            return ReadInventory(store, (object)mailbox.GetRules(), token);
        }, token);

    // Outlook collections start at 1. Preserve unreadable and unsupported parts for the import report.
    public static OutlookRuleInventory ReadInventory(OutlookRuleStore store, object collection, CancellationToken token)
    {
        dynamic rules = collection;
        var items = new List<OutlookSourceRule>();
        for (int i = 1, count = rules.Count; i <= count; i++)
        {
            token.ThrowIfCancellationRequested();
            dynamic rule = rules.Item(i);
            bool? local;
            try { local = (bool)rule.IsLocalRule; }
            catch (Exception e) when (e is COMException or RuntimeBinderException) { local = null; }
            items.Add(new((string)rule.Name, (int)rule.ExecutionOrder, (bool)rule.Enabled, local, (int)rule.RuleType,
                Parts((object)rule.Conditions, false), Parts((object)rule.Exceptions, false), Parts((object)rule.Actions, true)));
        }
        return new(1, store.Profile, store.Name, items.ToArray());
    }

    private static OutlookRulePart[] Parts(object collection, bool actions)
    {
        dynamic parts = collection;
        var rows = new List<OutlookRulePart>();
        for (int i = 1, count = parts.Count; i <= count; i++)
        {
            int type = -1;
            try
            {
                dynamic part = parts.Item(i);
                if (!(bool)part.Enabled) continue;
                type = actions ? (int)part.ActionType : (int)part.ConditionType;
                var field = (actions,type) switch
                {
                    (true,19) => "mark-read", (true,21) => "stop", (true,1) => "move", (true,5) => "copy",
                    (true,2) => "categories", (false,2) => "subject", (false,13) => "body",
                    (false,14) => "subject-or-body", (false,16) => "recipient-address", (false,17) => "sender-address",
                    _ => "unsupported"
                };
                string[] values = field switch
                {
                    "categories" => Words((object?)part.Categories),
                    "move" or "copy" => [(string)part.Folder.FolderPath,(string)part.Folder.EntryID,(string)part.Folder.StoreID],
                    "mark-read" or "stop" or "unsupported" => [],
                    _ => Words((object?)part.Text)
                };
                bool supported = field is "mark-read" or "stop" || (field is not ("move" or "copy" or "unsupported") && values.Length > 0);
                rows.Add(new(i,type,field,values,supported,supported ? "" : "请核对原规则或选择本地目标文件夹。"));
            }
            catch (Exception e) when (e is COMException or RuntimeBinderException or InvalidCastException)
            {
                rows.Add(new(i,type,"unavailable",[],false,"Outlook 未提供此项内容。"));
            }
        }
        return rows.ToArray();
    }

    private static string[] Words(object? value) => value switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => [s],
        Array a => a.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
        _ => []
    };

    private static Task<T> InOpenSession<T>(Func<dynamic,T> read, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("读取已打开的 Outlook 需要 Windows。");
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                dynamic application = RunningOutlook();
                T value = read((object)application.Session);
                token.ThrowIfCancellationRequested();
                result.TrySetResult(value);
            }
            catch (OperationCanceledException) { result.TrySetCanceled(token); }
            catch (COMException e) { result.TrySetException(new InvalidOperationException("请打开经典 Outlook，并确认这个邮箱支持规则。",e)); }
            catch (Exception e) { result.TrySetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static object RunningOutlook()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var type = Type.GetTypeFromProgID("Outlook.Application") ?? throw new InvalidOperationException("本机没有安装经典 Outlook。");
        var id = type.GUID;
        GetActiveObject(ref id, IntPtr.Zero, out var app);
        return app;
    }

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid id, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object application);
}
