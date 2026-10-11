// PC Remote - Tray Host & Application Context
// Manages system tray NotifyIcon, main GUI window (AgentForm), server lifecycle, and Windows startup registration.

namespace PcRemoteAgent;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray = new();
    private readonly AgentForm _mainForm;
    private readonly ToolStripMenuItem _codeItem = new("Pairing code: —") { Enabled = false };
    private readonly ToolStripMenuItem _devicesItem = new("No devices connected") { Enabled = false };
    private readonly ToolStripMenuItem _startupItem =
        new("Run at startup") { CheckOnClick = true, Checked = StartupToggle.IsEnabled() };
    private readonly CancellationTokenSource _cts = new();

    public TrayApplicationContext(bool startMinimized = false)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _mainForm = new AgentForm();
        MainForm = _mainForm;

        var menu = new ContextMenuStrip();
        _ = menu.Handle; // Force HWND creation on UI thread for thread-safe BeginInvoke
        menu.Items.Add(new ToolStripMenuItem("Open PC Remote", null, (_, _) => ShowGuiWindow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_codeItem);
        menu.Items.Add(new ToolStripMenuItem("Copy pairing code", null, (_, _) => CopyToClipboard(Program.CurrentPairingCode)));
        menu.Items.Add(_devicesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add("Open logs folder", null, (_, _) => AgentLog.OpenLogsFolder());
        menu.Items.Add("Check for updates...", null, async (_, _) =>
        {
            // An async void handler must never let an exception escape: WinForms
            // rethrows it on the message loop and the process dies.
            try
            {
                var res = await AppUpdater.CheckForUpdateAsync();
                if (res.UpdateAvailable)
                {
                    var choice = MessageBox.Show(
                        $"A new version ({res.LatestVersion}) of PC Remote Agent is available!\n\nDo you want to download and update now?",
                        "Update Available",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);
                    if (choice == DialogResult.Yes)
                    {
                        await AppUpdater.DownloadAndApplyUpdateAsync(res.DownloadUrl, res.ChecksumsUrl);
                    }
                }
                else
                {
                    MessageBox.Show("You are running the latest version of PC Remote Agent.", "PC Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"[!] Update check failed: {ex.GetType().Name}: {ex.Message}");
                MessageBox.Show(
                    $"Could not check for updates.\n\n{ex.Message}",
                    "PC Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray.Icon = LoadIcon();
        _tray.Text = "PC Remote — starting…";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowGuiWindow();

        // Detached in Dispose: the static events outlive the tray icon and
        // would otherwise invoke into a torn-down UI.
        Program.PairingCodeChanged += OnPairingCodeChanged;
        Program.ConnectedCountChanged += OnConnectedCountChanged;

        _startupItem.CheckedChanged += (_, _) => StartupToggle.Set(_startupItem.Checked);

        Application.ApplicationExit += (_, _) => ShutdownServer();

        _ = Task.Run(() => Program.StartServerAsync(_cts.Token))
            .ContinueWith(t => OnUi(() =>
            {
                var reason = t.Exception?.GetBaseException().Message ?? "unknown error";
                _tray.Text = "PC Remote — error";
                _tray.ShowBalloonTip(6000, "PC Remote", $"Agent failed to start: {reason}", ToolTipIcon.Error);
            }), TaskContinuationOptions.OnlyOnFaulted);

        if (!startMinimized)
        {
            ShowGuiWindow();
        }
    }

    private void OnPairingCodeChanged(string code) => OnUi(() =>
    {
        _codeItem.Text = $"Pairing code: {code}";
        _tray.Text = $"PC Remote — {Environment.MachineName}";
    });

    private void OnConnectedCountChanged(int n) => OnUi(() =>
        _devicesItem.Text = n == 1 ? "1 device connected" : $"{n} devices connected");

    private void ShowGuiWindow()
    {
        if (_mainForm.IsDisposed) return;
        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.BringToFront();
        _mainForm.Activate();
    }

    /// <summary>Clipboard writes throw when another process holds the clipboard
    /// open — never take the tray down for it.</summary>
    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"[!] Could not write to the clipboard: {ex.GetType().Name}: {ex.Message}");
        }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Program.PairingCodeChanged -= OnPairingCodeChanged;
            Program.ConnectedCountChanged -= OnConnectedCountChanged;
            _cts.Dispose();
            _tray.Dispose();
            _mainForm.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// "Run at startup" persisted in the HKCU Run key: user level (no admin),
/// points at the installed executable path in LocalAppData.
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
        string exePath = InstallerHelper.InstalledExePath;
        if (!File.Exists(exePath)) exePath = Environment.ProcessPath ?? Application.ExecutablePath;

        if (enabled)
            key.SetValue(ValueName, $"\"{exePath}\" --minimized");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
