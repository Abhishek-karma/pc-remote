// PC Remote Tray - GUI window. Dark control panel fed by service IPC status;
// no local server state. Update logic lives in the service (signature-verified).

using PcRemote.Core;

namespace PcRemote.Tray;

internal sealed class AgentForm : Form
{
    private readonly Label _lblStatusDot;
    private readonly Label _lblStatusText;
    private readonly Label _lblPairingCode;
    private readonly Label _lblDevicesCount;
    private readonly Label _lblPcState;
    private readonly ComboBox _cmbIpAddresses;
    private readonly CheckBox _chkMinimizeToTray;

    public AgentForm()
    {
        Text = "PC Remote";
        ClientSize = new Size(460, 430);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(24, 26, 38);
        ForeColor = Color.White;
        Icon = LoadFormIcon();

        // 1. Header Panel
        var pnlHeader = new Panel
        {
            Location = new Point(16, 14),
            Size = new Size(428, 50),
            BackColor = Color.FromArgb(34, 37, 54)
        };

        var lblAppTitle = new Label
        {
            Text = "PC Remote",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(235, 240, 245),
            Location = new Point(12, 10),
            AutoSize = true
        };

        _lblStatusDot = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(180, 180, 180),
            Location = new Point(270, 14),
            AutoSize = true
        };

        _lblStatusText = new Label
        {
            Text = "Checking service…",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(180, 190, 205),
            Location = new Point(292, 16),
            AutoSize = true
        };

        pnlHeader.Controls.Add(lblAppTitle);
        pnlHeader.Controls.Add(_lblStatusDot);
        pnlHeader.Controls.Add(_lblStatusText);

        // 2. Pairing Code Card
        var pnlPairing = new Panel
        {
            Location = new Point(16, 76),
            Size = new Size(428, 120),
            BackColor = Color.FromArgb(34, 37, 54)
        };

        var lblPairingHeader = new Label
        {
            Text = "PAIRING CODE",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 150, 170),
            Location = new Point(14, 12),
            AutoSize = true
        };

        _lblPairingCode = new Label
        {
            Text = "— — — — — —",
            Font = new Font("Consolas", 26f, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 180, 250),
            Location = new Point(12, 34),
            Size = new Size(230, 48),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var btnCopyCode = CreateButton("Copy Code", new Point(252, 40), new Size(80, 32));
        btnCopyCode.Click += (_, _) =>
        {
            if (_lblPairingCode.Tag is string code && !string.IsNullOrEmpty(code))
            {
                Clipboard.SetText(code);
                btnCopyCode.Text = "Copied!";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { btnCopyCode.Text = "Copy Code"; t.Stop(); t.Dispose(); };
                t.Start();
            }
        };

        var btnNewCode = CreateButton("New Code", new Point(338, 40), new Size(76, 32));
        btnNewCode.Click += async (_, _) =>
        {
            var code = await ServiceIpc.GeneratePairingCodeAsync();
            if (code is not null) UpdatePairingCode(code);
            else MessageBox.Show(
                "Generating a pairing code requires an elevated PC Remote window.\n\n" +
                "Close the tray and run PCRemoteTray.exe as administrator.",
                "PC Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        var lblPairingHint = new Label
        {
            Text = "Code valid for 5 minutes. Enter this code in the Android app to pair.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = Color.FromArgb(150, 160, 175),
            Location = new Point(14, 88),
            Size = new Size(400, 24)
        };

        pnlPairing.Controls.Add(lblPairingHeader);
        pnlPairing.Controls.Add(_lblPairingCode);
        pnlPairing.Controls.Add(btnCopyCode);
        pnlPairing.Controls.Add(btnNewCode);
        pnlPairing.Controls.Add(lblPairingHint);

        // 3. Network & Connection Card
        var pnlNetwork = new Panel
        {
            Location = new Point(16, 208),
            Size = new Size(428, 125),
            BackColor = Color.FromArgb(34, 37, 54)
        };

        var lblNetworkHeader = new Label
        {
            Text = "NETWORK & CONNECTED DEVICES",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 150, 170),
            Location = new Point(14, 12),
            AutoSize = true
        };

        _lblDevicesCount = new Label
        {
            Text = "0 device(s) connected",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Location = new Point(14, 36),
            AutoSize = true
        };

        _lblPcState = new Label
        {
            Text = "PC state: —",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(180, 190, 205),
            Location = new Point(14, 60),
            AutoSize = true
        };

        _cmbIpAddresses = new ComboBox
        {
            Location = new Point(14, 88),
            Size = new Size(220, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(48, 52, 74),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        PopulateIpAddresses();

        var btnCopyIp = CreateButton("Copy IP", new Point(242, 86), new Size(80, 30));
        btnCopyIp.Click += (_, _) =>
        {
            var ip = _cmbIpAddresses.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(ip))
            {
                Clipboard.SetText(ip.Split(':')[0]);
                btnCopyIp.Text = "Copied!";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { btnCopyIp.Text = "Copy IP"; t.Stop(); t.Dispose(); };
                t.Start();
            }
        };

        var btnUnpair = CreateButton("Unpair All", new Point(328, 86), new Size(86, 30));
        btnUnpair.Click += (_, _) =>
        {
            var res = MessageBox.Show(
                "Are you sure you want to unpair all trusted devices? Paired phones will need to re-enter a pairing code.",
                "Unpair All Devices",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (res == DialogResult.Yes)
            {
                var ok = ServiceIpc.RevokeAllDevices();
                MessageBox.Show(ok
                    ? "All paired devices have been unpaired."
                    : "Unpair requires an elevated PC Remote window (Run as administrator).",
                    "PC Remote", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        };

        pnlNetwork.Controls.Add(lblNetworkHeader);
        pnlNetwork.Controls.Add(_lblDevicesCount);
        pnlNetwork.Controls.Add(_lblPcState);
        pnlNetwork.Controls.Add(_cmbIpAddresses);
        pnlNetwork.Controls.Add(btnCopyIp);
        pnlNetwork.Controls.Add(btnUnpair);

        // 4. Footer
        var pnlPrefs = new Panel
        {
            Location = new Point(16, 345),
            Size = new Size(428, 70),
            BackColor = Color.FromArgb(34, 37, 54)
        };

        _chkMinimizeToTray = new CheckBox
        {
            Text = "Minimize to system tray on close (X)",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(220, 225, 235),
            Location = new Point(14, 12),
            Size = new Size(380, 24),
            Checked = true
        };

        var btnOpenLogs = CreateButton("Open Logs Folder", new Point(14, 38), new Size(130, 28));
        btnOpenLogs.Click += (_, _) => AgentLog.OpenLogsFolder();

        pnlPrefs.Controls.Add(_chkMinimizeToTray);
        pnlPrefs.Controls.Add(btnOpenLogs);

        Controls.Add(pnlHeader);
        Controls.Add(pnlPairing);
        Controls.Add(pnlNetwork);
        Controls.Add(pnlPrefs);
    }

    /// <summary>Refreshed by the tray's IPC poll loop.</summary>
    public void UpdateStatus(ServiceIpc.Status? status)
    {
        if (status is null)
        {
            _lblStatusDot.ForeColor = Color.FromArgb(220, 90, 90);
            _lblStatusText.Text = "Service not running";
            _lblPairingCode.Text = "— — — — — —";
            _lblPairingCode.Tag = null;
            _lblDevicesCount.Text = "0 device(s) connected";
            _lblPcState.Text = "Start the PCRemoteService or reinstall PC Remote.";
        }
        else
        {
            _lblStatusDot.ForeColor = status.ServiceState == "running"
                ? Color.FromArgb(76, 187, 120)
                : Color.FromArgb(230, 170, 60);
            _lblStatusText.Text = status.ServiceState == "running"
                ? "Active (Port 58642)"
                : status.ServiceState;
            UpdatePairingCode(status.PairingCode);
            _lblDevicesCount.Text = status.ConnectedDevices switch
            {
                null or 0 => "0 device(s) connected",
                1 => "1 device connected",
                var n => $"{n} devices connected",
            };
            _lblPcState.Text = $"PC state: {status.SessionState ?? "unknown"}";
        }
    }

    private void UpdatePairingCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        _lblPairingCode.Tag = code;
        _lblPairingCode.Text = FormatPairingCode(code);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _chkMinimizeToTray.Checked)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    private void PopulateIpAddresses()
    {
        _cmbIpAddresses.Items.Clear();
        foreach (var ip in NetworkInfo.GetLocalIPv4Addresses())
        {
            _cmbIpAddresses.Items.Add($"{ip}:58642");
        }
        if (_cmbIpAddresses.Items.Count > 0)
        {
            _cmbIpAddresses.SelectedIndex = 0;
        }
        else
        {
            _cmbIpAddresses.Items.Add("No active LAN IP found");
            _cmbIpAddresses.SelectedIndex = 0;
        }
    }

    private static string FormatPairingCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6) return "— — — — — —";
        return $"{code[..3]} {code[3..]}";
    }

    private static Button CreateButton(string text, Point location, Size size)
    {
        return new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 57, 80),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            UseVisualStyleBackColor = false
        };
    }

    private static Icon LoadFormIcon()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            return Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}
