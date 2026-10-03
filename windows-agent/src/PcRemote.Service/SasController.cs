// SasController — secure attention sequence.
// Ctrl+Alt+Del is NOT an ordinary keyboard combination: only the OS can
// raise it. The documented API for software SAS generation is SendSAS
// (sas.dll), callable from a SYSTEM service — exactly this process.

using System.Runtime.InteropServices;

namespace PcRemote.Service;

public static class SasController
{
    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool asUser);

    /// <summary>Raises the secure attention sequence in the active console
    /// session. asUser=false injects the SAS as the service (the supported
    /// software-SAS path); the shell then shows the secure screen.</summary>
    public static void SendSas()
    {
        try
        {
            SendSAS(asUser: false);
            Console.WriteLine("[+] SAS sent");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] SendSAS failed: {ex.Message}");
        }
    }
}
