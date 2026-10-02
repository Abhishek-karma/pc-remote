// PC Remote Tray - UI client over service IPC.
//
// The tray owns no server, no pairing store and no input engine anymore
// (requirement 1/11): it displays service status, asks the service for new
// pairing codes, and offers update/unpair actions that the service executes
// (with its own elevation checks). The tray's only persisted state is the
// HKCU Run entry controlling ITS OWN autostart — the service itself is
// managed by the SCM and starts with Windows regardless of the tray.

using System.Diagnostics;
using System.IO;
using PcRemote.Core;

namespace PcRemote.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray = new();
    private readonly AgentForm _mainForm;
    private readonly ToolStripMenuItem _codeItem = new("Pairing code: —") { Enabled = false };
    private readonly ToolStripMenuItem _devicesItem = new("No devices connected") { Enabled = false };
    private readonly ToolStripMenuItem _stateItem = new("Service state: —") { Enabled = false };
    private readonly ToolStripMenuItem _startupItem =
        new("Show tray at startup") { CheckOnClick = true, Checked = StartupToggle.IsEnabled() };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 3000 };

    public TrayApplicationContext(bool startMinimized = false)
    {
        _mainForm = new AgentForm();
        MainForm = _mainForm;

        var menu = new ContextMenuStrip();
        _ = menu.Handle; // Force HWND creation on UI thread for thread-safe BeginInvoke
        menu.Items.Add(new ToolStripMenuItem("Open PC Remote", null, (_, _) => ShowGuiWindow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_codeItem);
        menu.Items.Add("Copy pairing code", null, (_, _) => CopyText(ServiceIpc.GetStatus()?.PairingCode ?? ""));
        menu.Items.Add("New pairing code", null, async (_, _) => await ServiceIpc.GeneratePairingCodeAsync());
        menu.Items.Add(_devicesItem);
        menu.Items.Add(_stateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add("Open logs folder", null, (_, _) => AgentLog.OpenLogsFolder());
        menu.Items.Add("Check for updates...", null, async (_, _) => await CheckUpdatesAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray.Icon = LoadIcon();
        _tray.Text = "PC Remote — checking service…";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowGuiWindow();

        _startupItem.CheckedChanged += (_, _) => StartupToggle.Set(_startupItem.Checked);

        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _pollTimer.Start();

        _ = RefreshStatusAsync();

        if (!startMinimized)
        {
            ShowGuiWindow();
        }
    }

    private async Task RefreshStatusAsync()
    {
        var status = await ServiceIpc.GetStatusAsync();
        RunOnUi(() =>
        {
            if (status is null)
            {
                _tray.Text = "PC Remote — service not running";
                _codeItem.Text = "Pairing code: —";
                _devicesItem.Text = "No devices connected";
                _stateItem.Text = "Service state: not running";
                _mainForm.UpdateStatus(null);
                return;
            }

            _tray.Text = $"PC Remote — {Environment.MachineName}";
            // The service only discloses the pairing code to an elevated
            // caller, so an unelevated tray explains how to get one instead of
            // showing a blank field.
            _codeItem.Text = string.IsNullOrEmpty(status.PairingCode)
                ? "Pairing code: (run as administrator)"
                : $"Pairing code: {status.PairingCode}";
            _devicesItem.Text = status.ConnectedDevices switch
            {
                null or 0 => "No devices connected",
                1 => "1 device connected",
                var n => $"{n} devices connected",
            };
            _stateItem.Text = $"Service state: {status.ServiceState} · PC state: {status.SessionState}";
            _mainForm.UpdateStatus(status);
        });
    }

    private async Task CheckUpdatesAsync()
    {
        var res = await ServiceIpc.CheckForUpdateAsync();
        RunOnUi(() =>
        {
            if (res is null)
            {
                MessageBox.Show("The PC Remote service is not running.", "PC Remote",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (res.State == "update_available")
            {
                var choice = MessageBox.Show(
                    $"A new version ({res.Version}) is available.\n\nDownload and install now? The service verifies the package signature before installing.",
                    "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (choice == DialogResult.Yes)
                {
                    var apply = ServiceIpc.ApplyUpdate();
                    MessageBox.Show(apply == true
                        ? "Update started. The service will install the verified package and restart."
                        : "Update could not start (elevation or service required).",
                        "PC Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            else
            {
                MessageBox.Show("You are running the latest version of PC Remote.", "PC Remote",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        });
    }

    private static void CopyText(string? text)
    {
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void ShowGuiWindow()
    {
        if (_mainForm.IsDisposed) return;
        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.BringToFront();
        _mainForm.Activate();
        _ = RefreshStatusAsync();
    }

    /// <summary>The exe's embedded brand icon, with a safe fallback.</summary>
    private static Icon LoadIcon()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            return Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        catch { return SystemIcons.Application; }
    }

    private void RunOnUi(Action action)
    {
        var menu = _tray.ContextMenuStrip;
        if (menu is { IsDisposed: false, IsHandleCreated: true } && menu.InvokeRequired)
        {
            menu.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private void Exit()
    {
        _pollTimer.Stop();
        _tray.Visible = false;
        Application.Exit();
    }
}

/// <summary>IPC facade for the tray UI. All calls are short round trips.</summary>
internal static class ServiceIpc
{
    public sealed record Status(string? PairingCode, int? ConnectedDevices, string? ServiceState, string? SessionState);

    public static Status? GetStatus()
    {
        try
        {
            using var client = new IpcClient();
            client.Connect(1500);
            var reply = client.RoundTrip(new IpcMessage { Type = "status", Role = "tray" }, 2000);
            if (reply is not { Ok: true }) return null;
            return new Status(reply.PairingCode, reply.ConnectedDevices, reply.ServiceState, reply.SessionState);
        }
        catch
        {
            return null;
        }
    }

    public static Task<Status?> GetStatusAsync() => Task.Run(GetStatus);

    public static async Task<string?> GeneratePairingCodeAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                using var client = new IpcClient();
                client.Connect(2000);
                var reply = client.RoundTrip(new IpcMessage { Type = "generate_pairing_code", Role = "tray" }, 2500);
                return reply?.PairingCode;
            });
        }
        catch { return null; }
    }

    public sealed record UpdateCheck(string State, string Version);

    public static Task<UpdateCheck?> CheckForUpdateAsync() => Task.Run(() =>
    {
        try
        {
            using var client = new IpcClient();
            client.Connect(8000);
            var reply = client.RoundTrip(new IpcMessage { Type = "update_check", Role = "tray" }, 10000);
            if (reply is not { Ok: true }) return null;
            return new UpdateCheck(reply.ServiceState ?? "up_to_date", reply.Version ?? "");
        }
        catch { return null; }
    });

    public static bool? ApplyUpdate()
    {
        try
        {
            using var client = new IpcClient();
            client.Connect(3000);
            var reply = client.RoundTrip(new IpcMessage { Type = "update_apply", Role = "tray" }, 5000);
            return reply?.Ok;
        }
        catch { return null; }
    }

    public static bool RevokeAllDevices()
    {
        try
        {
            using var client = new IpcClient();
            client.Connect(3000);
            var reply = client.RoundTrip(new IpcMessage { Type = "revoke_all_devices", Role = "tray" }, 5000);
            return reply?.Ok == true;
        }
        catch { return false; }
    }
}

/// <summary>
/// Tray autostart (HKCU Run): purely cosmetic — it starts the tray UI after
/// logon. The service does not depend on this entry at all.
/// </summary>
internal static class StartupToggle
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PC Remote Tray";

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
            key.SetValue(ValueName, $"\"{exePath}\" --minimized");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
