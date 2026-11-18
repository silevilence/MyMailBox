using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace MyMailBox.Desktop;

internal static class PageFeedback
{
    internal static Control Layout(Panel form,TextBlock status,Control? action=null)
    {
        form.Children.Remove(status);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);
        var layout=new DockPanel();var footer=new StackPanel{Spacing=4};
        if(action is not null)footer.Children.Add(action);
        footer.Children.Add(new ScrollViewer{Content=status,MaxHeight=116,Margin=new Thickness(0,4,0,0)});
        DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);layout.Children.Add(new ScrollViewer{Content=form});return layout;
    }
}
