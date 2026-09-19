// PC Remote - tray host
//
// The agent's user-facing shell (Phase B): a NotifyIcon with the live pairing
// code, connected-device count, a "Run at startup" toggle (HKCU Run key —
// user level, no admin rights), a logs shortcut, and Exit. A balloon shows
// the pairing code on start and on every rotation. All server lifecycle is
// in Program.StartServerAsync/StopServer — this class only marshals events
// to the UI thread and renders them.

namespace PcRemoteAgent;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _codeItem = new("Pairing code: —") { Enabled = false };
    private readonly ToolStripMenuItem _devicesItem = new("No devices connected") { Enabled = false };
    private readonly ToolStripMenuItem _startupItem =
        new("Run at startup") { CheckOnClick = true, Checked = StartupToggle.IsEnabled() };
    private readonly CancellationTokenSource _cts = new();

    public TrayApplicationContext()
    {
        // WinForms installs this context as soon as the first control (the
        // NotifyIcon) is created — capture it to marshal server events.
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        var menu = new ContextMenuStrip();
        _ = menu.Handle; // Force HWND creation on UI thread for thread-safe BeginInvoke
        menu.Items.Add(
            new ToolStripMenuItem($"PC Remote {Program.VersionDisplay}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_codeItem);
        menu.Items.Add("Copy pairing code", null, (_, _) => Clipboard.SetText(Program.CurrentPairingCode));
        menu.Items.Add(_devicesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add("Open logs folder", null, (_, _) => AgentLog.OpenLogsFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray.Icon = LoadIcon();
        _tray.Text = "PC Remote — starting…";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowCode();

        Program.PairingCodeChanged += code => OnUi(() =>
        {
            _codeItem.Text = $"Pairing code: {code}";
            _tray.Text = $"PC Remote — {Environment.MachineName}";
            ShowCode();
        });
        Program.ConnectedCountChanged += n => OnUi(() =>
            _devicesItem.Text = n == 1 ? "1 device connected" : $"{n} devices connected");

        _startupItem.CheckedChanged += (_, _) => StartupToggle.Set(_startupItem.Checked);

        Application.ApplicationExit += (_, _) => ShutdownServer();

        _ = Task.Run(() => Program.StartServerAsync(_cts.Token))
            .ContinueWith(t => OnUi(() =>
            {
                var reason = t.Exception?.GetBaseException().Message ?? "unknown error";
                _tray.Text = "PC Remote — error";
                _tray.ShowBalloonTip(6000, "PC Remote", $"Agent failed to start: {reason}", ToolTipIcon.Error);
            }), TaskContinuationOptions.OnlyOnFaulted);
    }

    private void ShowCode()
    {
        // Code is already visible in context menu _codeItem.Text; balloon removed to avoid leaking code to Windows Notification Center history.
    }

    /// <summary>The exe's embedded brand icon, with a safe fallback.</summary>
    private static Icon LoadIcon()
    {
        try {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            return Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        catch { return SystemIcons.Application; }
    }

    private void OnUi(Action action)
    {
        var menu = _tray.ContextMenuStrip;
        if (menu is { IsDisposed: false, IsHandleCreated: true } && menu.InvokeRequired)
        {
            menu.BeginInvoke(action);
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    private void Exit()
    {
        ShutdownServer();
        _tray.Visible = false;
        Application.Exit();
    }

    private void ShutdownServer()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
        Program.StopServer();
    }
}

/// <summary>
/// "Run at startup" persisted in the HKCU Run key: user level (no admin),
/// points at the current executable path.
/// </summary>
internal static class StartupToggle
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PC Remote Agent";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        if (enabled)
            key.SetValue(ValueName, $"\"{exePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

