using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace FTFlash;

public sealed class MainForm : Form
{
    private sealed record Item(string Name, double Value)
    {
        public override string ToString() => Name;
    }

    // ---- connection ------------------------------------------------------------------
    private readonly ComboBox cmbDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly Button btnRefresh = new() { Text = "Refresh", AutoSize = true };
    private readonly ComboBox cmbClock = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly Button btnConnect = new() { Text = "Connect", AutoSize = true };

    // ---- chip ------------------------------------------------------------------------
    private readonly Button btnReadId = new() { Text = "Read ID", AutoSize = true };
    private readonly Label lblChip = new() { Text = "Not connected", AutoSize = true, Margin = new Padding(8, 7, 3, 0) };
    private readonly ComboBox cmbSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };

    // ---- file / buffer ---------------------------------------------------------------
    private readonly Button btnOpenFile = new() { Text = "Open File", AutoSize = true };
    private readonly Button btnSaveFile = new() { Text = "Save File", AutoSize = true };
    private readonly Label lblBuffer = new() { Text = "Buffer: empty", AutoSize = true, Margin = new Padding(8, 7, 3, 0) };

    // ---- operations ------------------------------------------------------------------
    private readonly Button btnReadIc = new() { Text = "Read IC", AutoSize = true };
    private readonly Button btnErase = new() { Text = "Erase", AutoSize = true };
    private readonly Button btnUnprotect = new() { Text = "Unprotect", AutoSize = true };
    private readonly Button btnProgram = new() { Text = "Program", AutoSize = true };
    private readonly Button btnVerify = new() { Text = "Verify", AutoSize = true };
    private readonly Button btnAuto = new() { Text = "Unprotect + Erase + Program + Verify", AutoSize = true };
    private readonly Button btnCancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly CheckBox chkTurbo = new() { Text = "Turbo program (timed pages, auto-checked)", Checked = true, AutoSize = true };

    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly Label lblStatus = new() { Text = "Idle", AutoSize = true };
    private readonly TextBox txtLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9f)
    };

    // ---- hex viewer ------------------------------------------------------------------
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage tabLog = new("Log");
    private readonly TabPage tabHex = new("Hex viewer");
    private readonly HexView hex = new() { Dock = DockStyle.Fill };
    private readonly TextBox txtGoto = new() { Width = 90 };
    private readonly Button btnGoto = new() { Text = "Go", AutoSize = true };
    private readonly ComboBox cmbFindMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly TextBox txtFind = new() { Width = 200 };
    private readonly Button btnFind = new() { Text = "Find Next", AutoSize = true };
    private readonly Label lblHexInfo = new() { Text = "No data", AutoSize = true, Margin = new Padding(6, 4, 3, 4) };

    // ---- state -----------------------------------------------------------------------
    private Ft232hSpi? _spi;
    private SpiFlash? _flash;
    private CancellationTokenSource? _cts;
    private List<DeviceInfo> _devices = new();
    private int _detected;               // capacity detected from JEDEC ID (0 = unknown)
    private bool _busy;
    private byte[]? _buffer;             // image in memory (opened file or chip dump)
    private string _bufferName = "";

    public MainForm()
    {
        Text = "Maruti - FT232H SPI Flash Programmer";
        Icon = LoadIcon();
        ClientSize = new Size(1300, 960);
        MinimumSize = new Size(820, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        foreach (var (n, hz) in new[] { ("30 MHz", 30e6), ("15 MHz", 15e6), ("10 MHz", 10e6),
                                        ("6 MHz", 6e6), ("3 MHz", 3e6), ("1 MHz", 1e6), ("500 kHz", 500e3) })
            cmbClock.Items.Add(new Item(n, hz));
        cmbClock.SelectedIndex = 0;

        cmbSize.Items.Add(new Item("Auto (JEDEC ID)", 0));
        foreach (var (n, b) in new[] { ("128 KB", 1 << 17), ("256 KB", 1 << 18), ("512 KB", 1 << 19),
                                       ("1 MB", 1 << 20), ("2 MB", 1 << 21), ("4 MB", 1 << 22),
                                       ("8 MB", 1 << 23), ("16 MB", 1 << 24), ("32 MB", 1 << 25),
                                       ("64 MB", 1 << 26) })
            cmbSize.Items.Add(new Item(n, b));
        cmbSize.SelectedIndex = 0;

        btnAuto.BackColor = Color.FromArgb(220, 235, 255);

        BuildLayout();
        WireEvents();
        WireHex();
        RefreshDevices();
        UpdateState();
        Log("Wiring: AD0=SCK  AD1=MOSI(DI)  AD2=MISO(DO)  AD3=CS  GND=GND. FT232H is 1.8 V or 3.3 V logic.");
        Log("Chip pins /WP and /HOLD must be tied high (1.8 V or 3.3 V) for standard SPI.");
    }

    // ---- layout ----------------------------------------------------------------------
    private static GroupBox Group(string title, params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        flow.Controls.AddRange(controls);
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true };
        g.Controls.Add(flow);
        return g;
    }

    private static Label L(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 7, 3, 0) };

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(lblStatus, 0, 0);
        bottom.Controls.Add(bar, 0, 1);
        bottom.Controls.Add(BuildTabs(), 0, 2);

        root.Controls.Add(BuildBanner(), 0, 0);
        root.Controls.Add(Group("Connection", L("Device:"), cmbDevice, btnRefresh, L("Clock:"), cmbClock, btnConnect), 0, 1);
        root.Controls.Add(Group("Chip", btnReadId, lblChip, L("Size:"), cmbSize), 0, 2);
        root.Controls.Add(Group("File / buffer", btnOpenFile, btnSaveFile, lblBuffer), 0, 3);
        root.Controls.Add(Group("Operations", btnReadIc, btnErase, btnUnprotect, btnProgram, btnVerify,
                                btnAuto, btnCancel, chkTurbo), 0, 4);
        root.Controls.Add(bottom, 0, 5);
        Controls.Add(root);
    }

    private void WireEvents()
    {
        btnRefresh.Click += (_, _) => RefreshDevices();
        btnConnect.Click += (_, _) => OnConnect();
        btnReadId.Click += (_, _) => OnReadId();
        btnOpenFile.Click += (_, _) => OnOpenFile();
        btnSaveFile.Click += (_, _) => OnSaveFile();
        btnReadIc.Click += async (_, _) => await OnReadIc();
        btnErase.Click += async (_, _) => await OnErase();
        btnUnprotect.Click += async (_, _) => await OnUnprotect();
        btnProgram.Click += async (_, _) => await OnProgram();
        btnVerify.Click += async (_, _) => await OnVerify();
        btnAuto.Click += async (_, _) => await OnAuto();
        btnCancel.Click += (_, _) => { _cts?.Cancel(); Log("Cancelling..."); };
        FormClosing += (_, _) =>
        {
            _cts?.Cancel();
            _spi?.Dispose();
        };
    }

    // ---- logging / state -------------------------------------------------------------
    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(msg)); return; }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }

    private void Status(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => Status(msg)); return; }
        lblStatus.Text = msg;
    }

    private void SetMarquee(bool on)
    {
        if (InvokeRequired) { Invoke(() => SetMarquee(on)); return; }
        bar.Style = on ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (!on) bar.Value = 0;
    }

    private void UpdateState()
    {
        bool conn = _spi?.IsOpen == true;
        bool hasBuf = _buffer != null;

        btnConnect.Text = conn ? "Disconnect" : "Connect";
        btnConnect.Enabled = !_busy && (conn || cmbDevice.Items.Count > 0);
        cmbDevice.Enabled = !conn && !_busy;
        btnRefresh.Enabled = !conn && !_busy;
        cmbClock.Enabled = !conn && !_busy;

        btnOpenFile.Enabled = !_busy;
        btnSaveFile.Enabled = !_busy && hasBuf;

        btnReadId.Enabled = conn && !_busy;
        cmbSize.Enabled = conn && !_busy;
        btnReadIc.Enabled = conn && !_busy;
        btnErase.Enabled = conn && !_busy;
        btnUnprotect.Enabled = conn && !_busy;
        btnProgram.Enabled = conn && !_busy && hasBuf;
        btnVerify.Enabled = conn && !_busy && hasBuf;
        btnAuto.Enabled = conn && !_busy && hasBuf;
        btnCancel.Enabled = _busy;

        lblBuffer.Text = hasBuf
            ? $"Buffer: {_bufferName}  ({_buffer!.Length:N0} bytes)"
            : "Buffer: empty (Open File or Read IC)";
    }

    private void ShowError(string msg) =>
        MessageBox.Show(this, msg, "FTFlash", MessageBoxButtons.OK, MessageBoxIcon.Error);

    // ---- connection ------------------------------------------------------------------
    private void RefreshDevices()
    {
        cmbDevice.Items.Clear();
        try
        {
            _devices = Ft232hSpi.Enumerate();
            foreach (var d in _devices) cmbDevice.Items.Add(d);
            if (cmbDevice.Items.Count > 0) cmbDevice.SelectedIndex = 0;
            Log(_devices.Count == 0 ? "No FT232H found." : $"Found {_devices.Count} device(s).");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            ShowError(ex.Message);
        }
        UpdateState();
    }

    private void OnConnect()
    {
        if (_spi?.IsOpen == true)
        {
            _spi.Dispose();
            _spi = null;
            _flash = null;
            _detected = 0;
            lblChip.Text = "Not connected";
            Log("Disconnected (pins released).");
            UpdateState();
            return;
        }

        if (cmbDevice.SelectedItem is not DeviceInfo dev) return;
        double hz = ((Item)cmbClock.SelectedItem!).Value;
        try
        {
            _spi = new Ft232hSpi();
            _spi.Open(dev.Index, hz);
            _flash = new SpiFlash(_spi);
            Log($"Connected to {dev} at {hz / 1e6:0.###} MHz.");
            UpdateState();
            OnReadId();
        }
        catch (Exception ex)
        {
            _spi?.Dispose();
            _spi = null;
            _flash = null;
            Log("ERROR: " + ex.Message);
            ShowError(ex.Message);
            UpdateState();
        }
    }

    // ---- Read ID ---------------------------------------------------------------------
    private void OnReadId()
    {
        if (_flash == null) return;
        try
        {
            var (m, t, c) = _flash.ReadId();
            if ((m == 0x00 && t == 0x00 && c == 0x00) || (m == 0xFF && t == 0xFF && c == 0xFF))
            {
                _detected = 0;
                lblChip.Text = "No chip detected (ID 00/FF)";
                Log($"JEDEC ID = {m:X2} {t:X2} {c:X2} -> no response. Check wiring, power, clip contact and clock speed.");
                return;
            }
            _detected = SpiFlash.CapacityFromId(c);
            string size = _detected > 0 ? FormatSize(_detected) : "unknown size";
            lblChip.Text = $"{SpiFlash.ManufacturerName(m)}  ID {m:X2} {t:X2} {c:X2}  ({size})";
            Log($"JEDEC ID = {m:X2} {t:X2} {c:X2}  {SpiFlash.ManufacturerName(m)}, {size}");
            if (_detected == 0) Log("Capacity not decodable from ID - choose the size manually.");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            ShowError(ex.Message);
        }
    }

    private static string FormatSize(int bytes) =>
        bytes >= 1 << 20 ? $"{bytes >> 20} MB" : $"{bytes >> 10} KB";

    private int GetCapacity()
    {
        if (cmbSize.SelectedItem is Item it && it.Value > 0) return (int)it.Value;
        if (_detected > 0) return _detected;
        throw new InvalidOperationException("Unknown flash size. Click Read ID, or choose the size manually.");
    }

    // ---- branding (icon + logo embedded in the exe) -----------------------------------
    private static Stream? Res(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("FTFlash.Assets." + name);

    private static Icon? LoadIcon()
    {
        try { using var s = Res("app.ico"); return s == null ? null : new Icon(s); }
        catch { return null; }
    }

    private static Image? LoadLogo()
    {
        try
        {
            using var s = Res("logo.png");
            if (s == null) return null;
            using var tmp = new Bitmap(s);
            return new Bitmap(tmp);          // independent copy, stream can be closed
        }
        catch { return null; }
    }

    private Control BuildBanner()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(14, 34, 72), Margin = new Padding(0, 0, 0, 6) };
        var pic = new PictureBox
        {
            Image = LoadLogo(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(55, 55), Location = new Point(8, 6), BackColor = Color.Transparent
        };
        var title = new Label
        {
            Text = "MARUTI", AutoSize = true, ForeColor = Color.White, BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 13f), Location = new Point(76, 2)
        };
        var sub = new Label
        {
            Text = "FT232H SPI Flash Programmer", AutoSize = true, BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(244, 186, 60), Font = new Font("Segoe UI", 9f), Location = new Point(79, 36)
        };
        panel.Controls.AddRange(new Control[] { pic, title, sub });
        return panel;
    }

    // ---- hex viewer --------------------------------------------------------------------
    private Control BuildTabs()
    {
        cmbFindMode.Items.AddRange(new object[] { "Hex bytes", "Text" });
        cmbFindMode.SelectedIndex = 0;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        toolbar.Controls.AddRange(new Control[] { L("Go to (hex):"), txtGoto, btnGoto, L("Find:"), cmbFindMode, txtFind, btnFind });

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(toolbar, 0, 0);
        table.Controls.Add(hex, 0, 1);
        table.Controls.Add(lblHexInfo, 0, 2);

        tabLog.Controls.Add(txtLog);
        tabHex.Controls.Add(table);
        tabs.TabPages.Add(tabLog);
        tabs.TabPages.Add(tabHex);
        return tabs;
    }

    private void WireHex()
    {
        hex.CursorChanged += off => lblHexInfo.Text = hex.Describe(off);
        btnGoto.Click += (_, _) => DoGoto();
        btnFind.Click += (_, _) => DoFind();
        txtGoto.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoGoto(); } };
        txtFind.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoFind(); } };
    }

    private void ShowInHex()
    {
        hex.SetData(_buffer);
        lblHexInfo.Text = _buffer == null
            ? "No data"
            : $"{_buffer.Length:N0} bytes - click a byte for details, use Go to / Find to navigate";
        tabs.SelectedTab = tabHex;
    }

    private void DoGoto()
    {
        if (_buffer == null) { lblHexInfo.Text = "Buffer is empty."; return; }
        string t = txtGoto.Text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int off) ||
            off < 0 || off >= _buffer.Length)
        {
            lblHexInfo.Text = $"Invalid address. Valid range: 0 - {_buffer.Length - 1:X} (hex).";
            return;
        }
        hex.GoTo(off);
    }

    private void DoFind()
    {
        if (_buffer == null) { lblHexInfo.Text = "Buffer is empty."; return; }
        byte[] pattern;
        try
        {
            if (cmbFindMode.SelectedIndex == 0)
            {
                string t = txtFind.Text.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
                                       .Replace(" ", "").Replace(",", "");
                pattern = Convert.FromHexString(t);
            }
            else
            {
                pattern = Encoding.Latin1.GetBytes(txtFind.Text);
            }
        }
        catch
        {
            lblHexInfo.Text = "Invalid hex pattern (example: DE AD BE EF).";
            return;
        }
        if (pattern.Length == 0) return;

        int idx = hex.Find(pattern);
        lblHexInfo.Text = idx >= 0 ? $"Found at 0x{idx:X8}  ({pattern.Length} byte match)" : "Not found.";
    }

    // ---- Open File / Save File -------------------------------------------------------
    private void OnOpenFile()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Binary file (*.bin;*.rom;*.img)|*.bin;*.rom;*.img|All files (*.*)|*.*"
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _buffer = File.ReadAllBytes(ofd.FileName);
            _bufferName = Path.GetFileName(ofd.FileName);
            Log($"Opened {ofd.FileName} ({_buffer.Length:N0} bytes).");
            ShowInHex();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        UpdateState();
    }

    private void OnSaveFile()
    {
        if (_buffer == null) return;
        using var sfd = new SaveFileDialog
        {
            Filter = "Binary file (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = string.IsNullOrEmpty(_bufferName) ? "flash_dump.bin" : _bufferName
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(sfd.FileName, _buffer);
            Log($"Saved {_buffer.Length:N0} bytes to {sfd.FileName}");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ---- operation runner ------------------------------------------------------------
    private async Task RunOp(string name, Action<IProgress<int>, CancellationToken> work, bool indeterminate = false, long bytes = 0)
    {
        _busy = true;
        UpdateState();
        _cts = new CancellationTokenSource();
        bar.Style = indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        bar.Value = 0;
        var progress = new Progress<int>(v => { if (bar.Style == ProgressBarStyle.Continuous) bar.Value = Math.Clamp(v, 0, 100); });
        var token = _cts.Token;
        var sw = Stopwatch.StartNew();
        Status(name + "...");
        Log(name + "...");
        try
        {
            await Task.Run(() => work(progress, token));
            string speed = bytes > 0 && sw.Elapsed.TotalSeconds > 0
                ? $" ({bytes / 1048576.0 / sw.Elapsed.TotalSeconds:F2} MB/s)" : "";
            Log($"{name} finished in {sw.Elapsed.TotalSeconds:F1} s{speed}.");
            Status("Done");
        }
        catch (OperationCanceledException)
        {
            Log($"{name} cancelled.");
            Status("Cancelled");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            Status("Error");
            ShowError(ex.Message);
        }
        finally
        {
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = 0;
            _busy = false;
            _cts.Dispose();
            _cts = null;
            UpdateState();
        }
    }

    // ---- Read IC ---------------------------------------------------------------------
    private async Task OnReadIc()
    {
        if (_flash == null) return;
        int cap;
        try { cap = GetCapacity(); }
        catch (Exception ex) { ShowError(ex.Message); return; }

        var flash = _flash;
        flash.Capacity = cap;
        byte[]? result = null;
        await RunOp($"Read IC ({FormatSize(cap)})", (p, ct) =>
        {
            result = flash.Read(0, cap, p, ct);
        }, bytes: cap);

        if (result != null)
        {
            _buffer = result;
            _bufferName = "chip_dump.bin";
            ShowInHex();
            UpdateState();
            Log("Chip contents are now in the buffer. Use Save File to write them to disk.");
        }
    }

    // ---- Erase (whole chip) ----------------------------------------------------------
    private async Task OnErase()
    {
        if (_flash == null) return;
        try { _flash.Capacity = GetCapacity(); }
        catch (Exception ex) { ShowError(ex.Message); return; }

        if (MessageBox.Show(this, "Erase the ENTIRE chip? All data will be lost.", "Confirm erase",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        var flash = _flash;
        await RunOp("Erase", (p, ct) => flash.EraseChip(ct), indeterminate: true);
    }

    // ---- Unprotect -------------------------------------------------------------------
    private async Task OnUnprotect()
    {
        if (_flash == null) return;
        var flash = _flash;
        await RunOp("Unprotect", (p, ct) =>
        {
            flash.WriteStatus(0x00, ct);
            byte sr = flash.ReadStatus();
            Log($"Status register = 0x{sr:X2}" + ((sr & 0x1C) != 0
                ? "  (block-protect bits still set - check /WP pin or SRP lock)"
                : "  (block-protect cleared)"));
        }, indeterminate: true);
    }

    // ---- Program (erases only the sectors that need it) ------------------------------
    private bool BufferFits(out int cap)
    {
        cap = 0;
        try { cap = GetCapacity(); }
        catch (Exception ex) { ShowError(ex.Message); return false; }
        if (_buffer == null || _buffer.Length == 0) { ShowError("Buffer is empty. Open a file first."); return false; }
        if (_buffer.Length > cap)
        {
            ShowError($"Buffer ({_buffer.Length:N0} bytes) is larger than the flash ({cap:N0} bytes).");
            return false;
        }
        return true;
    }

    private async Task OnProgram()
    {
        if (_flash == null || !BufferFits(out int cap)) return;
        if (MessageBox.Show(this, $"Program {_buffer!.Length:N0} bytes?\n\nOnly sectors that differ are erased and rewritten.",
                "Confirm program", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        var flash = _flash;
        var data = _buffer;
        bool turbo = chkTurbo.Checked;
        flash.Capacity = cap;
        await RunOp("Program", (p, ct) => ProgramCore(flash, data, turbo, p, ct), bytes: data.Length);
    }

    private void ProgramCore(SpiFlash flash, byte[] data, bool turbo, IProgress<int> p, CancellationToken ct)
    {
        var st = flash.SmartWrite(data, turbo, Status, Log, p, ct);
        if (st.NothingToDo) return;
        Log($"Programmed {st.Pages} pages, erased {st.SectorsErased} sectors" +
            (turbo ? $", {st.Retries} retries, page wait {st.FinalDelayUs:F0} us." : "."));
    }

    // ---- Verify ----------------------------------------------------------------------
    private async Task OnVerify()
    {
        if (_flash == null || !BufferFits(out int cap)) return;
        var flash = _flash;
        var data = _buffer!;
        flash.Capacity = cap;
        await RunOp("Verify", (p, ct) =>
        {
            flash.Verify(data, p, ct);
            Log("Verify OK - flash matches buffer.");
        }, bytes: data.Length);
    }

    // ---- Unprotect + Erase + Program + Verify in one click ---------------------------
    private async Task OnAuto()
    {
        if (_flash == null || !BufferFits(out int cap)) return;
        if (MessageBox.Show(this,
                $"Unprotect, ERASE THE ENTIRE CHIP, program {_buffer!.Length:N0} bytes and verify?\n\nAll existing data will be lost.",
                "Confirm auto program", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        var flash = _flash;
        var data = _buffer;
        bool turbo = chkTurbo.Checked;
        flash.Capacity = cap;

        await RunOp("Auto", (p, ct) =>
        {
            Status("1/4 Unprotecting...");
            SetMarquee(true);
            flash.WriteStatus(0x00, ct);
            Log($"Unprotected, status = 0x{flash.ReadStatus():X2}");

            Status("2/4 Erasing chip...");
            flash.EraseChip(ct);
            SetMarquee(false);

            Status("3/4 Programming...");
            ProgramCore(flash, data, turbo, p, ct);

            Status("4/4 Verifying...");
            flash.Verify(data, p, ct);
            Log("Verify OK - flash matches buffer.");
        }, indeterminate: false, bytes: data.Length);
    }
}
