// PC Remote Tray - entry point.
//
// The tray is a status surface only. Everything it shows comes from the service
// over the named pipe; the service owns pairing, discovery and input.

using PcRemote.Core;

namespace PcRemote.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The tray logs to ProgramData, next to the service, so there is one
        // place to look when a user reports a problem.
        Log.Init(PairingStore.LogDir, "tray");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayApplicationContext(startMinimized: args.Contains("--minimized")));
    }
}