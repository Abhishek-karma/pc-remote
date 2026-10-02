// PC Remote Tray - entry point.

using PcRemote.Core;

namespace PcRemote.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AgentLog.Init(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCRemote", "logs"));

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayApplicationContext(startMinimized: args.Contains("--minimized")));
    }
}
