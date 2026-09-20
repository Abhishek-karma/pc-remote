// PC Remote Windows Agent - GUI Main Form
// Clean modern dark-themed control window for monitoring agent status, pairing code, connected devices, and network IPs.

using System.Drawing;
using System.Windows.Forms;

namespace PcRemoteAgent;

internal sealed class AgentForm : Form
{
    private readonly Label _lblStatusDot;
    private readonly Label _lblStatusText;
    private readonly Label _lblPairingCode;
    private readonly Label _lblDevicesCount;
    private readonly ComboBox _cmbIpAddresses;
    private readonly CheckBox _chkStartup;
    private readonly CheckBox _chkMinimizeToTray;

    public AgentForm()
    {
        Text = $"PC Remote Agent v{Program.VersionDisplay}";
        ClientSize = new Size(460, 530);
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
            Text = "PC Remote Agent",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(235, 240, 245),
            Location = new Point(12, 10),
            AutoSize = true
        };

        _lblStatusDot = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(76, 187, 120),
            Location = new Point(270, 14),
            AutoSize = true
        };

        _lblStatusText = new Label
        {
            Text = $"Active (Port {Program.Port})",
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
            Size = new Size(428, 140),
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
            Text = FormatPairingCode(Program.CurrentPairingCode),
            Font = new Font("Consolas", 26f, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 180, 250),
            Location = new Point(12, 34),
            Size = new Size(230, 48),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var btnCopyCode = CreateButton("Copy Code", new Point(252, 40), new Size(80, 32));
        btnCopyCode.Click += (_, _) =>
        {
            var code = Program.CurrentPairingCode;
            if (!string.IsNullOrEmpty(code))
            {
                Clipboard.SetText(code);
                btnCopyCode.Text = "Copied!";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { btnCopyCode.Text = "Copy Code"; t.Stop(); t.Dispose(); };
                t.Start();
            }
        };

        var btnNewCode = CreateButton("New Code", new Point(338, 40), new Size(76, 32));
        btnNewCode.Click += (_, _) => Program.GenerateNewPairingCode();

        var lblPairingHint = new Label
        {
            Text = "Code valid for 5 minutes. Enter this code in the Android app to pair.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = Color.FromArgb(150, 160, 175),
            Location = new Point(14, 98),
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
            Location = new Point(16, 228),
            Size = new Size(428, 145),
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

        var lblIpLabel = new Label
        {
            Text = "Local IP address (for manual connect):",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(160, 170, 185),
            Location = new Point(14, 66),
            AutoSize = true
        };

        _cmbIpAddresses = new ComboBox
        {
            Location = new Point(14, 90),
            Size = new Size(220, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(48, 52, 74),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        PopulateIpAddresses();

        var btnCopyIp = CreateButton("Copy IP", new Point(242, 88), new Size(80, 30));
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

        var btnUnpair = CreateButton("Unpair All", new Point(328, 88), new Size(86, 30));
        btnUnpair.Click += (_, _) =>
        {
            var res = MessageBox.Show(
                "Are you sure you want to unpair all trusted devices? Paired phones will need to re-enter a pairing code.",
                "Unpair All Devices",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (res == DialogResult.Yes)
            {
                Program.RevokeAllTokens();
                MessageBox.Show("All paired devices have been unpaired.", "PC Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        pnlNetwork.Controls.Add(lblNetworkHeader);
        pnlNetwork.Controls.Add(_lblDevicesCount);
        pnlNetwork.Controls.Add(lblIpLabel);
        pnlNetwork.Controls.Add(_cmbIpAddresses);
        pnlNetwork.Controls.Add(btnCopyIp);
        pnlNetwork.Controls.Add(btnUnpair);

        // 4. Preferences & Action Footer
        var pnlPrefs = new Panel
        {
            Location = new Point(16, 385),
            Size = new Size(428, 120),
            BackColor = Color.FromArgb(34, 37, 54)
        };

        _chkStartup = new CheckBox
        {
            Text = "Run automatically when Windows starts",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(220, 225, 235),
            Location = new Point(14, 12),
            Size = new Size(380, 24),
            Checked = StartupToggle.IsEnabled()
        };
        _chkStartup.CheckedChanged += (_, _) => StartupToggle.Set(_chkStartup.Checked);

        _chkMinimizeToTray = new CheckBox
        {
            Text = "Minimize to system tray on close (X)",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(220, 225, 235),
            Location = new Point(14, 40),
            Size = new Size(380, 24),
            Checked = true
        };

        var btnOpenLogs = CreateButton("Open Logs Folder", new Point(14, 76), new Size(130, 30));
        btnOpenLogs.Click += (_, _) => AgentLog.OpenLogsFolder();

        var btnCheckUpdates = CreateButton("Check Updates", new Point(152, 76), new Size(114, 30));
        btnCheckUpdates.Click += async (_, _) =>
        {
            btnCheckUpdates.Text = "Checking...";
            btnCheckUpdates.Enabled = false;
            var res = await AppUpdater.CheckForUpdateAsync();
            btnCheckUpdates.Enabled = true;
            btnCheckUpdates.Text = "Check Updates";

            if (res.UpdateAvailable)
            {
                var choice = MessageBox.Show(
                    $"A new version ({res.LatestVersion}) of PC Remote Agent is available!\n\nDo you want to download and update now?",
                    "Update Available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (choice == DialogResult.Yes)
                {
                    btnCheckUpdates.Text = "Updating...";
                    btnCheckUpdates.Enabled = false;
                    await AppUpdater.DownloadAndApplyUpdateAsync(res.DownloadUrl);
                }
            }
            else
            {
                MessageBox.Show("You are running the latest version of PC Remote Agent.", "PC Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var btnHideTray = CreateButton("Hide to Tray", new Point(310, 76), new Size(104, 30));
        btnHideTray.Click += (_, _) => Hide();

        pnlPrefs.Controls.Add(_chkStartup);
        pnlPrefs.Controls.Add(_chkMinimizeToTray);
        pnlPrefs.Controls.Add(btnOpenLogs);
        pnlPrefs.Controls.Add(btnCheckUpdates);
        pnlPrefs.Controls.Add(btnHideTray);

        Controls.Add(pnlHeader);
        Controls.Add(pnlPairing);
        Controls.Add(pnlNetwork);
        Controls.Add(pnlPrefs);

        // Event subscriptions for dynamic updates
        Program.PairingCodeChanged += code => BeginInvoke(() => _lblPairingCode.Text = FormatPairingCode(code));
        Program.ConnectedCountChanged += count => BeginInvoke(() =>
            _lblDevicesCount.Text = count == 1 ? "1 device connected" : $"{count} device(s) connected");
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
        foreach (var ip in Program.GetLocalIPv4Addresses())
        {
            _cmbIpAddresses.Items.Add($"{ip}:{Program.Port}");
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
