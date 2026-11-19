using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using System.Runtime.InteropServices;

internal static class AccessibilityUiChecks
{
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern nint LoadKeyboardLayout(string name,uint flags);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd,uint message,nint wParam,nint lParam);
    public static void Run(Window window,Ui ui,Guid sender,string workspace)
    {
        void Activate(string id)
        {
            var button = ui.Find(id);
            button.Focus();
            Keyboard.Type(VirtualKeyShort.RETURN);
        }
        var original=window.BoundingRectangle;var dpi=GetDpiForWindow(window.Properties.NativeWindowHandle.Value);
        double scale=dpi/96d;
        window.Focus();
        // These Latin keyboard fixtures exercise Return as a command, after text composition is complete.
        var keyboard = LoadKeyboardLayout("00000409", 0);
        if (keyboard == 0) throw new Exception("无法载入用于英文键盘验收的布局。");
        SendMessage(window.Properties.NativeWindowHandle.Value, 0x0050, 0, keyboard);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL,VirtualKeyShort.KEY_F);
        var input=ui.Find("search-value").AsTextBox();if(!SpinWait.SpinUntil(()=>input.Properties.HasKeyboardFocus.Value,4000))throw new Exception("搜索快捷键未将焦点移入条件字段。");
        Keyboard.Type("absent-accessibility-fixture");Keyboard.Type(VirtualKeyShort.RETURN);ui.Status("search-status","没有匹配邮件");
        Keyboard.Type(VirtualKeyShort.ESCAPE);ui.Status("search-status","已取消");
        var transform=window.Patterns.Transform.Pattern;
        try
        {
            transform.Resize(650*scale,485*scale);
            var navigation=ui.Find("compact-navigation").AsComboBox();
            Keyboard.Type(VirtualKeyShort.F6);if(!SpinWait.SpinUntil(()=>navigation.Properties.HasKeyboardFocus.Value,4000))throw new Exception("窄窗口主导航无键盘焦点。");
            Keyboard.Type(VirtualKeyShort.F6);if(navigation.Properties.HasKeyboardFocus.Value)throw new Exception("焦点不能离开主导航。");
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL,VirtualKeyShort.KEY_N);
            var to=ui.Find("compose-to");if(string.IsNullOrWhiteSpace(to.Name)||!SpinWait.SpinUntil(()=>to.Properties.HasKeyboardFocus.Value,4000))throw new Exception("写信快捷键或收件人标签缺失。");
            Keyboard.Type(VirtualKeyShort.TAB);if(!SpinWait.SpinUntil(()=>ui.Find("compose-cc").Properties.HasKeyboardFocus.Value,4000))throw new Exception("写信字段顺序错误。");
            ui.Find("compose-subject").Focus();Keyboard.Type("Keyboard draft");
            Activate("compose-save");ui.Status("compose-status","草稿已保存");
            var feedback=ui.Find("compose-status");var outer=window.BoundingRectangle;var status=feedback.BoundingRectangle;
            if(status.IsEmpty||status.Left<outer.Left-1||status.Right>outer.Right+1||status.Bottom>outer.Bottom+1)throw new Exception("窄窗口状态反馈在窗口外。");
            Console.WriteLine($"PASS keyboard search/empty/cancel, F6 regions, compose focus order, named controls and responsive 650-DIP layout at actual {dpi} DPI");
        }
        finally{transform.Resize(original.Width,original.Height);}

        Activate("nav-mail-selectors/choose");
        var choice = ui.Find("choice-"+sender).AsCheckBox();
        choice.Focus();if (choice.IsChecked != true) Keyboard.Type(VirtualKeyShort.SPACE);
        Activate("selection-save");ui.Status("selection-status","固定选择已保存");
        Activate("selection-restore");ui.Status("selection-status","已恢复固定集合");

        string source = Path.Combine(workspace, "keyboard-import.eml");
        File.WriteAllText(source, "From: user@example.test\r\nSubject: Keyboard import\r\n\r\nKeyboard import body");
        Activate("nav-local-mail/inbox");ui.Text("mail-file",source+".missing");Activate("mail-import");
        ui.Status("mail-status","请先选择存在的 EML 文件");
        ui.Text("mail-file",source);Activate("mail-import");ui.Body("Keyboard import body");

        Activate("nav-accounts/automation");
        var plans = ui.Find("auto-plans").AsComboBox();plans.Expand();
        plans.Items.Single(item => item.Text=="旧邮件测试").Select();plans.Collapse();
        Activate("auto-load");ui.Text("auto-name","");Activate("auto-save");ui.Status("auto-status","请填写任务名");
        ui.Text("auto-name","旧邮件测试");Activate("auto-preview");ui.Status("auto-status","未满足");
        Console.WriteLine("PASS keyboard identity selection, EML import/read after correcting a missing path, automation preview after correcting invalid input");
    }
}
