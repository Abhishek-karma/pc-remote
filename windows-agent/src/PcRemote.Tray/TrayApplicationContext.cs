// PC Remote tray.
//
// A small status menu, and nothing else. It owns no server, no pairing store and
// no input engine: it asks the service for the pairing code and for status, and
// can ask it to revoke devices. The service does the work and applies its own
// privilege checks.
//
// The context menu IS the whole UI. The service only discloses the pairing code
// to an ELEVATED caller, so an unelevated tray says how to get it rather than
// showing an empty value.

using PcRemote.Core;

namespace PcRemote.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _codeItem = new("Pairing code: -") { Enabled = false };
    private readonly ToolStripMenuItem _stateItem = new("Service: checking...") { Enabled = false };
    private readonly ToolStripMenuItem _desktopItem = new("Desktop: -") { Enabled = false };
    private readonly ToolStripMenuItem _startupItem =
        new("Show tray at startup") { CheckOnClick = true, Checked = StartupToggle.IsEnabled() };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 3000 };

    public TrayApplicationContext(bool startMinimized = false)
    {
        var menu = new ContextMenuStrip();
        _ = menu.Handle; // forces HWND creation on the UI thread

        menu.Items.Add(_codeItem);
        menu.Items.Add("Copy pairing code", null, (_, _) => CopyText(ServiceIpc.Status()?.PairingCode ?? ""));
        menu.Items.Add("New pairing code", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_stateItem);
        menu.Items.Add(_desktopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Revoke all paired phones", null, (_, _) => RevokeAll());
        menu.Items.Add(_startupItem);
        menu.Items.Add("Open logs folder", null, (_, _) => Log.OpenLogsFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray.Icon = LoadIcon();
        _tray.Text = "PC Remote";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;

        _startupItem.CheckedChanged += (_, _) => StartupToggle.Set(_startupItem.Checked);
        _poll.Tick += async (_, _) => await RefreshAsync();
        _poll.Start();

        _ = RefreshAsync();

        // First run shows the menu with the code in it: that one thing is what
        // the user needs to read off this PC.
        if (!startMinimized) menu.Show(Cursor.Position);
    }

    private async Task RefreshAsync()
    {
        var status = await Task.Run(ServiceIpc.Status);
        RunOnUi(() =>
        {
            if (status is null)
            {
                _stateItem.Text = "Service: not running";
                _desktopItem.Text = "Desktop: -";
                _codeItem.Text = "Pairing code: -";
                _tray.Text = "PC Remote - service not running";
                return;
            }

            _stateItem.Text = $"Service: running ({status.ConnectedDevices} connected)";
            _desktopItem.Text = $"Desktop: {status.SessionState}";

            if (status.PairingCode is { Length: > 0 })
            {
                _codeItem.Text = $"Pairing code: {status.PairingCode}";
                _tray.Text = "PC Remote - running";
            }
            else
            {
                // The service withheld it because this tray is unelevated.
                _codeItem.Text = "Pairing code: run as administrator to see";
            }
        });
    }

    private void RevokeAll()
    {
        var answer = MessageBox.Show(
            "Forget every paired phone? Each will have to pair again using a new code.",
            "PC Remote", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;

        if (!ServiceIpc.RevokeAll())
        {
            MessageBox.Show(
                "Could not reach the service, or this tray is not running as administrator.",
                "PC Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ = RefreshAsync();
    }

    private static void CopyText(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Console.WriteLine($"[!] could not copy: {ex.Message}"); }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
        }
        catch (Exception) { return SystemIcons.Application; }
    }

    private void RunOnUi(Action action)
    {
        var menu = _tray.ContextMenuStrip;
        if (menu is { IsDisposed: false, IsHandleCreated: true } && menu.InvokeRequired)
            menu.BeginInvoke(action);
        else
            action();
    }

    private void Exit()
    {
        _poll.Stop();
        _tray.Visible = false;
        Application.Exit();
    }
}

/// <summary>Status shown in the tray, read from the service over the named pipe.</summary>
internal sealed record ServiceStatus(string? PairingCode, int ConnectedDevices, string? SessionState);

/// <summary>Short, bounded round trips to the service. Never throws: a tray that
/// crashes when the service is restarting is worse than one showing "stopped".</summary>
internal static class ServiceIpc
{
    public static ServiceStatus? Status()
    {
        try
        {
            using var client = new IpcClient();
            var reply = client.RoundTrip(new IpcMessage { Type = "status" });
            return reply is { Ok: true }
                ? new ServiceStatus(reply.PairingCode, reply.ConnectedDevices, reply.SessionState)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool RevokeAll()
    {
        try
        {
            using var client = new IpcClient();
            return client.RoundTrip(new IpcMessage { Type = "revoke_all" }) is { Ok: true };
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Tray autostart (HKCU Run). Purely cosmetic: the SERVICE is managed by
/// the SCM and starts with Windows whether or not this tray ever runs.</summary>
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
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;

        if (enabled) key.SetValue(ValueName, $"\"{exe}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}