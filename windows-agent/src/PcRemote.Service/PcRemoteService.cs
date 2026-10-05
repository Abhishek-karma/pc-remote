// PC Remote Service - Windows service host.
//
// Privilege boundary: this process (LocalSystem) owns networking, pairing and
// authentication, session detection and the tray IPC endpoint. It never renders
// UI and never runs as the user. Input is delegated to PCRemoteInput, which the
// service launches in the interactive session.

using System.ServiceProcess;
using PcRemote.Core;

namespace PcRemote.Service;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // A service started by the SCM has no console, so without this every
        // diagnostic would be silently discarded.
        Log.Init(PairingStore.LogDir, "service");

        if (args.Contains("--console"))
        {
            Console.WriteLine("PC Remote service (console mode). Ctrl+C to stop.");
            var host = new PcRemoteService();
            host.StartHost();
            var stopped = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.TrySetResult(); };
            await stopped.Task;
            await host.StopHost();
            return;
        }

        ServiceBase.Run(new ServiceBase[] { new PcRemoteService() });
    }
}

public sealed class PcRemoteService : ServiceBase
{
    private readonly CancellationTokenSource _cts = new();
    private SessionManager? _sessions;
    private IpcServer? _ipc;
    private Task? _listener;

    public PcRemoteService()
    {
        ServiceName = "PCRemoteService";
        CanPauseAndContinue = false;
        CanShutdown = true;
    }

    public void StartHost()
    {
        Log.Info("service starting");

        var pairing = new PairingStore();
        var certificate = CertificateManager.LoadOrCreate();
        var pcId = CertificateManager.LoadOrCreatePcId();

        _sessions = new SessionManager();
        _sessions.Start();

        var channel = new ControlChannel(
            pairing,
            pcId,
            new InputRouter(_sessions),
            // Read by the control channel to tell the phone which desktop the PC
            // is showing; never read keystrokes or text.
            () => _sessions.State.ToString().ToLowerInvariant(),
            certificate);

        // Tray IPC: status, a fresh pairing code, and revoking devices. That is
        // the whole local control surface.
        _ipc = new IpcServer(
            IpcEndpoints.ControlPipe,
            request => HandleIpc(request, pairing, channel, _sessions));
        _ipc.Start();

        _listener = Task.Run(() => channel.RunAsync(_cts.Token), CancellationToken.None);
        Log.Info("service started");
    }

    public async Task StopHost()
    {
        Log.Info("service stopping");
        _cts.Cancel();
        _sessions?.Stop();
        if (_ipc is not null) await _ipc.DisposeAsync();
        try { if (_listener is not null) await _listener; } catch (OperationCanceledException) { }
        Log.Info("service stopped");
    }

    /// <summary>Handles one tray request. The pairing code is trust material, so it
    /// is only ever returned to a caller that passed the privilege check inside
    /// IpcServer.</summary>
    private static IpcMessage HandleIpc(
        IpcMessage request,
        PairingStore pairing,
        ControlChannel channel,
        SessionManager sessions)
    {
        switch (request.Type)
        {
            case "status":
                return new IpcMessage
                {
                    Type = "status",
                    Ok = true,
                    PairingCode = pairing.CurrentCode,
                    ConnectedDevices = channel.ConnectedCount,
                    SessionState = sessions.State.ToString().ToLowerInvariant(),
                };

            case "new_pairing_code":
                pairing.RotateCode();
                return new IpcMessage { Type = request.Type, Ok = true, PairingCode = pairing.CurrentCode };

            case "revoke_all":
                pairing.RevokeAll();
                return new IpcMessage { Type = request.Type, Ok = true };

            default:
                return new IpcMessage { Type = request.Type, Ok = false, Error = "unknown_request" };
        }
    }

    protected override void OnStart(string[] args) => StartHost();

    protected override void OnStop() => StopHost().GetAwaiter().GetResult();

    protected override void OnShutdown()
    {
        _cts.Cancel();
        base.OnShutdown();
    }
}
