using Avalonia.Controls;
using System.Diagnostics;
using System.Text.Json;

namespace MyMailBox.Desktop;

internal static class FrameMeasurements
{
    internal static void Attach(Window window, string workspace)
    {
        var ticks = new List<long>();
        bool closed = false;
        void Frame(TimeSpan frameTime)
        {
            if (closed) return;
            ticks.Add(Stopwatch.GetTimestamp());
            window.RequestAnimationFrame(Frame);
        }

        window.Opened += (_, _) => window.RequestAnimationFrame(Frame);
        window.Closed += (_, _) =>
        {
            closed = true;
            File.WriteAllText(Path.Combine(workspace, "frame-ticks.json"), JsonSerializer.Serialize(ticks));
        };
    }
}
