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

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Color.FromArgb(0x0F, 0x15, 0x20),
            ForeColor = Color.FromArgb(0xE2, 0xE8, 0xF0),
            ShowImageMargin = false
        };
        var titleItem = new ToolStripMenuItem($"PC Remote {Program.VersionDisplay}") { Enabled = false };
        titleItem.ForeColor = Color.FromArgb(0x38, 0xBD, 0xF8);

        menu.Items.Add(titleItem);
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

    private void ShowCode() =>
        _tray.ShowBalloonTip(4000, "PC Remote",
            $"Pairing code: {Program.CurrentPairingCode} (valid 5 minutes)", ToolTipIcon.None);

    /// <summary>The exe's embedded brand icon, with a safe fallback.</summary>
    private static Icon LoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    private void OnUi(Action action) => _ui.Post(_ => action(), null);

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
        if (enabled)
            key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(0x0F, 0x15, 0x20);
    public override Color MenuBorder => Color.FromArgb(0x26, 0x35, 0x4B);
    public override Color MenuItemSelected => Color.FromArgb(0x0C, 0x2D, 0x48);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x0C, 0x2D, 0x48);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x0C, 0x2D, 0x48);
    public override Color MenuItemBorder => Color.FromArgb(0x38, 0xBD, 0xF8);
    public override Color SeparatorDark => Color.FromArgb(0x26, 0x35, 0x4B);
    public override Color SeparatorLight => Color.FromArgb(0x16, 0x1F, 0x2E);
    public override Color ImageMarginGradientBegin => Color.FromArgb(0x0F, 0x15, 0x20);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(0x0F, 0x15, 0x20);
    public override Color ImageMarginGradientEnd => Color.FromArgb(0x0F, 0x15, 0x20);
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? (e.Item.Selected ? Color.FromArgb(0x38, 0xBD, 0xF8) : Color.FromArgb(0xE2, 0xE8, 0xF0))
            : Color.FromArgb(0x64, 0x74, 0x8B);
        base.OnRenderItemText(e);
    }
}
