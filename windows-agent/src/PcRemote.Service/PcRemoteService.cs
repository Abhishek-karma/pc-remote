// PC Remote Service - Windows service host.
//
// Privilege boundary: this process (LocalSystem) owns networking, auth/pairing,
// session detection, privileged operations and the WSS control channel. It never
// renders UI and never runs as the user. Desktop interaction is delegated to
// the PCRemoteSession helper (user token, normal desktop) and to the same
// helper in --secure-input mode (SYSTEM token, Winlogon desktop).

using System.ServiceProcess;
using PcRemote.Core;

namespace PcRemote.Service;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // Logging is initialized for BOTH entry paths: a service started by the
        // SCM has no console, so without this every diagnostic in the service,
        // the session manager and the updater is silently discarded.
        AgentLog.Init(PairingStore.ServiceLogDir);

        if (args.Contains("--console"))
        {
            // Developer mode: run the same logic as a console process.
            Console.WriteLine("=== PC Remote Service (console mode) ===");
            var svc = new PcRemoteService();
            await Task.Run(svc.StartHost);
            Console.WriteLine("Press Ctrl+C to stop.");
            var done = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
            await done.Task;
            await svc.StopHost();
            return;
        }

        ServiceBase.Run(new ServiceBase[] { new PcRemoteService() });
    }
}

public sealed class PcRemoteService : ServiceBase
{
    private readonly CancellationTokenSource _cts = new();
    private ControlChannel? _controlChannel;
    private SessionManager? _sessionManager;
    private InputRouter? _inputRouter;
    private IpcServer? _ipcServer;
    private Task? _hostTask;
    private PairingStore? _pairing;

    public PcRemoteService()
    {
        ServiceName = "PCRemoteService";
        CanPauseAndContinue = false;
        CanShutdown = true;
    }

    /// <summary>Starts the runtime; shared between SCM start and --console mode.</summary>
    public void StartHost()
    {
        Console.WriteLine("[*] PC Remote Service starting");

        var pairing = new PairingStore(PairingStore.DefaultServiceTokensFile, System.Security.Cryptography.DataProtectionScope.LocalMachine);
        var pcId = CertificateManager.LoadOrCreatePcId();
        var cert = CertificateManager.LoadOrCreate();
        if (!CertificateManager.IsUsableForServerAuth(cert))
        {
            Console.WriteLine("[!] TLS certificate unusable; refusing to serve without TLS");
            throw new InvalidOperationException("service certificate unusable");
        }

        _sessionManager = new SessionManager();
        _sessionManager.Start();

        _inputRouter = new InputRouter(_sessionManager);

        var ipc = new IpcCoordinator(pairing, _sessionManager);
        _ipcServer = new IpcServer(IpcEndpoints.ControlPipe, ipc.HandleIpc);
        _ipcServer.Start();

        _pairing = pairing;

        _controlChannel = new ControlChannel(pairing, pcId, _inputRouter, _sessionManager, cert);
        ipc.Channel = _controlChannel;
        _hostTask = Task.Run(() => _controlChannel.RunAsync(_cts.Token), CancellationToken.None);
    }

    public async Task StopHost()
    {
        Console.WriteLine("[*] PC Remote Service stopping");
        _cts.Cancel();
        _controlChannel?.Stop();
        try { if (_hostTask is not null) await _hostTask; } catch (OperationCanceledException) { }
        _sessionManager?.Stop();
        if (_ipcServer is not null) { try { await _ipcServer.DisposeAsync(); } catch { } }
    }

    protected override void OnStart(string[] args) => StartHost();

    protected override void OnStop() => StopHost().GetAwaiter().GetResult();

    protected override void OnShutdown()
    {
        _cts.Cancel();
        _controlChannel?.Stop();
        base.OnShutdown();
    }
}
