using System.Drawing.Drawing2D;
using System.Globalization;

public class Form1 : Form
{
    // ── Data ──────────────────────────────────────────────────────────
    private const int CHANNELS = 8;

    private double[] _times  = Array.Empty<double>();
    private int[,]   _states = new int[0, CHANNELS];   // [row, channel] 0 or 1
    private double   _totalUs = 0;

    // ── Raw events (source data — never filtered) ─────────────────────
    private (double t, int pin, int rising)[] _rawEvents     = Array.Empty<(double, int, int)>();
    private int[]                              _rawInitStates = new int[CHANNELS];

    // ── File stats (populated on load) ────────────────────────────────
    private string   _fileName    = "";
    private string   _fileFormat  = "";
    private long     _fileSizeBytes = 0;
    private int      _eventCount  = 0;          // raw edge events
    private bool[]   _activeCh    = new bool[CHANNELS];

    // ── View state ────────────────────────────────────────────────────
    private double _viewStart = 0;
    private double _viewEnd   = 1000;

    // ── Layout ────────────────────────────────────────────────────────
    private const int LABEL_W  = 50;   // left label column (px)
    private const int TIME_H   = 30;   // bottom time axis (px)
    private const int WAVE_TOP = 6;    // top margin so P0 isn't flush with panel edge

    // Lane height and signal padding scale with available space
    private int LaneH  => Math.Max(20, (_wavePanel?.Height - TIME_H - WAVE_TOP ?? 800) / CHANNELS);
    private int SigPad => Math.Max(3, LaneH / 5);

    private static readonly Color[] CH_COLOR =
    {
        Color.LimeGreen,
        Color.Cyan,
        Color.Yellow,
        Color.Magenta,
        Color.OrangeRed,
        Color.DeepSkyBlue,
        Color.LightGreen,
        Color.HotPink
    };

    private static readonly string[] CH_NAME =
        { "P0", "P1", "P2", "P3", "P4", "P5", "P6", "P7" };

    // ── Dark menu color table ─────────────────────────────────────────
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        static readonly Color _bg      = Color.FromArgb(40, 40, 40);
        static readonly Color _border  = Color.FromArgb(70, 70, 70);
        static readonly Color _hover   = Color.FromArgb(70, 70, 90);
        public override Color MenuItemSelected          => _hover;
        public override Color MenuItemBorder            => _border;
        public override Color MenuBorder                => _border;
        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin  => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd    => _bg;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd   => _hover;
        public override Color MenuItemPressedGradientBegin  => _hover;
        public override Color MenuItemPressedGradientEnd    => _hover;
        public override Color MenuStripGradientBegin    => Color.FromArgb(30, 30, 30);
        public override Color MenuStripGradientEnd      => Color.FromArgb(30, 30, 30);
    }

    // ── Double-buffered panel ─────────────────────────────────────────
    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint  |
                     ControlStyles.UserPaint, true);
            UpdateStyles();
        }
    }

    // ── Controls ──────────────────────────────────────────────────────
    private readonly DoubleBufferedPanel _wavePanel;
    private readonly HScrollBar _hScroll;
    private readonly Label      _cursorLabel;
    private readonly NumericUpDown          _glitchFilter;
    private readonly StatusStrip            _statusStrip;
    private readonly ToolStripStatusLabel   _slFile;
    private readonly ToolStripStatusLabel   _slEvents;
    private readonly ToolStripStatusLabel   _slSpan;
    private readonly ToolStripStatusLabel   _slRate;
    private readonly ToolStripStatusLabel   _slActive;
    private readonly ToolStripStatusLabel   _slView;
    private readonly ToolStripStatusLabel   _slSize;
    private readonly ToolStripStatusLabel   _slCursor;

    // ── Cached GDI resources (created once, reused every frame) ──────
    private Font         _labelFont  = null!;
    private Font         _axisFont   = null!;
    private Font         _cursorFont = null!;
    private Pen          _sepPen     = null!;
    private Pen          _borderPen  = null!;
    private Pen          _gridPen    = null!;
    private Pen          _tickPen    = null!;
    private SolidBrush   _textBrush  = null!;
    private SolidBrush   _bgBrush0   = null!;
    private SolidBrush   _bgBrush1   = null!;
    private Pen[]        _chPens     = null!;
    private SolidBrush[] _chBrushes  = null!;
    private Pen          _cursorPenA = null!;
    private Pen          _cursorPenB = null!;
    private SolidBrush   _cursorBrA  = null!;
    private SolidBrush   _cursorBrB  = null!;

    private bool   _dragging;
    private int    _dragStartX;
    private double _dragStartViewStart;
    private double _dragStartViewEnd;

    private double _cursorA        = double.NaN;
    private double _cursorB        = double.NaN;
    private int    _draggingCursor = 0;   // 0 = none, 1 = A, 2 = B

    // ── Constructor ───────────────────────────────────────────────────
    public Form1()
    {
        Text          = "Logic Analyser Viewer";
        Size          = new Size(1920, 1080);
        MinimumSize   = new Size(800, 400);
        BackColor     = Color.FromArgb(20, 20, 20);
        ForeColor     = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState   = FormWindowState.Maximized;

        // ── Menu bar ──
        var menu     = new MenuStrip { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
        var fileMenu = new ToolStripMenuItem("File") { ForeColor = Color.White, BackColor = Color.FromArgb(30, 30, 30) };
        var openItem = new ToolStripMenuItem("Open…")
        {
            ForeColor    = Color.White,
            BackColor    = Color.FromArgb(40, 40, 40),
            ShortcutKeys = Keys.Control | Keys.O
        };
        openItem.Click += (_, _) => OpenFile();
        fileMenu.DropDownItems.Add(openItem);
        fileMenu.DropDown.BackColor = Color.FromArgb(40, 40, 40);
        menu.Items.Add(fileMenu);
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());

        _cursorLabel = new Label
        {
            ForeColor = Color.FromArgb(160, 160, 160),
            BackColor = Color.Transparent,
            AutoSize  = true,
            Text      = "",
            Padding   = new Padding(0, 4, 8, 0)
        };
        menu.Items.Add(new ToolStripControlHost(_cursorLabel) { Alignment = ToolStripItemAlignment.Right });

        Controls.Add(menu);
        MainMenuStrip = menu;

        // ── Scrollbar ──
        _hScroll = new HScrollBar
        {
            Dock        = DockStyle.Bottom,
            Minimum     = 0,
            Maximum     = 10000,
            LargeChange = 1000,
            SmallChange = 100,
            Value       = 0
        };
        _hScroll.Scroll += OnScroll;
        Controls.Add(_hScroll);

        // ── Status bar ──
        static ToolStripStatusLabel MakeLabel(string text = "", int w = 0, bool spring = false) =>
            new ToolStripStatusLabel(text)
            {
                ForeColor  = Color.FromArgb(160, 160, 160),
                BackColor  = Color.FromArgb(25, 25, 25),
                Width      = w,
                Spring     = spring,
                AutoSize   = w == 0,
                TextAlign  = ContentAlignment.MiddleLeft,
                BorderSides = ToolStripStatusLabelBorderSides.Right,
                BorderStyle = Border3DStyle.Etched
            };

        _slFile   = MakeLabel("No file loaded", spring: true);
        _slEvents = MakeLabel("", 130);
        _slSpan   = MakeLabel("", 120);
        _slRate   = MakeLabel("", 120);
        _slActive = MakeLabel("", 180);
        _slView   = MakeLabel("", 130);
        _slSize   = MakeLabel("", 100);
        _slCursor = MakeLabel("", 200);

        _statusStrip = new StatusStrip
        {
            Dock          = DockStyle.Bottom,
            BackColor     = Color.FromArgb(25, 25, 25),
            ForeColor     = Color.FromArgb(160, 160, 160),
            SizingGrip    = false,
            ShowItemToolTips = true,
            Padding       = new Padding(1, 0, 0, 0)
        };
        _slFile.ToolTipText   = "Loaded file";
        _slEvents.ToolTipText = "Total edge events";
        _slSpan.ToolTipText   = "Total capture duration";
        _slRate.ToolTipText   = "Average event rate";
        _slActive.ToolTipText = "Channels with at least one transition";
        _slView.ToolTipText   = "Current view span";
        _slSize.ToolTipText   = "File size on disk";
        _slCursor.ToolTipText = "Time between cursors A and B";

        // Glitch filter — right-aligned in status bar
        _glitchFilter = new NumericUpDown
        {
            Minimum       = 0,
            Maximum       = 10000,
            Value         = 0,
            Width         = 65,
            DecimalPlaces = 0,
            BackColor     = Color.FromArgb(40, 40, 40),
            ForeColor     = Color.White,
            BorderStyle   = BorderStyle.FixedSingle
        };
        _glitchFilter.ValueChanged += (_, _) => { if (_rawEvents.Length > 0) ExpandAndApply(false); };

        var glitchLbl = new Label
        {
            Text      = "  Glitch ≥",
            ForeColor = Color.FromArgb(160, 160, 160),
            BackColor = Color.FromArgb(25, 25, 25),
            AutoSize  = true,
            TextAlign = ContentAlignment.MiddleRight,
            Padding   = new Padding(0, 4, 0, 0)
        };
        var glitchUnit = new Label
        {
            Text      = "µs  ",
            ForeColor = Color.FromArgb(160, 160, 160),
            BackColor = Color.FromArgb(25, 25, 25),
            AutoSize  = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(2, 4, 4, 0)
        };

        var glitchLblHost  = new ToolStripControlHost(glitchLbl)  { Alignment = ToolStripItemAlignment.Right };
        var glitchCtrlHost = new ToolStripControlHost(_glitchFilter) { Alignment = ToolStripItemAlignment.Right };
        var glitchUnitHost = new ToolStripControlHost(glitchUnit)  { Alignment = ToolStripItemAlignment.Right };

        _statusStrip.Items.AddRange(new ToolStripItem[]
        {
            _slFile, _slEvents, _slSpan, _slRate, _slActive, _slView, _slSize, _slCursor,
            glitchLblHost, glitchCtrlHost, glitchUnitHost
        });
        Controls.Add(_statusStrip);

        // ── Waveform panel ──
        _wavePanel = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _wavePanel.Paint      += OnPaint;
        _wavePanel.MouseWheel += OnMouseWheel;
        _wavePanel.MouseDown  += OnMouseDown;
        _wavePanel.MouseMove  += OnMouseMove;
        _wavePanel.MouseUp    += (_, _) => { _dragging = false; _draggingCursor = 0; };
        _wavePanel.MouseEnter += (_, _) => _wavePanel.Focus();
        _wavePanel.Resize     += (_, _) => _wavePanel.Invalidate();
        _wavePanel.KeyDown    += (_, e) =>
        {
            if (e.KeyCode == Keys.R && _totalUs > 0)
            {
                _cursorA = _viewStart + (_viewEnd - _viewStart) * 0.25;
                _cursorB = _viewStart + (_viewEnd - _viewStart) * 0.75;
                UpdateCursorStatus();
                _wavePanel.Invalidate();
            }
        };

        _wavePanel.AllowDrop = true;
        _wavePanel.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        _wavePanel.DragDrop += (_, e) =>
        {
            var files = (string[]?)e.Data?.GetData(DataFormats.FileDrop);
            if (files?.Length > 0) LoadFile(files[0]);
        };
        Controls.Add(_wavePanel);

        InitGdi();
    }

    // ── GDI resource management ───────────────────────────────────────
    private void InitGdi()
    {
        _labelFont  = new Font("Consolas", 9, FontStyle.Bold);
        _axisFont   = new Font("Consolas", 9);
        _cursorFont = new Font("Consolas", 8, FontStyle.Bold);
        _sepPen     = new Pen(Color.FromArgb(40, 40, 40));
        _borderPen  = new Pen(Color.FromArgb(50, 50, 50));
        _gridPen    = new Pen(Color.FromArgb(30, 30, 30));
        _tickPen    = new Pen(Color.FromArgb(80, 80, 80));
        _textBrush  = new SolidBrush(Color.FromArgb(150, 150, 150));
        _bgBrush0   = new SolidBrush(Color.FromArgb(10, 10, 10));
        _bgBrush1   = new SolidBrush(Color.FromArgb(16, 16, 16));
        _chPens    = new Pen[CHANNELS];
        _chBrushes = new SolidBrush[CHANNELS];
        for (int i = 0; i < CHANNELS; i++)
        {
            _chPens[i]    = new Pen(CH_COLOR[i], 1.5f);
            _chBrushes[i] = new SolidBrush(CH_COLOR[i]);
        }
        _cursorPenA = new Pen(Color.White,  1f) { DashStyle = DashStyle.Dash };
        _cursorPenB = new Pen(Color.Orange, 1f) { DashStyle = DashStyle.Dash };
        _cursorBrA  = new SolidBrush(Color.White);
        _cursorBrB  = new SolidBrush(Color.Orange);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont?.Dispose(); _axisFont?.Dispose(); _cursorFont?.Dispose();
            _sepPen?.Dispose(); _borderPen?.Dispose(); _gridPen?.Dispose(); _tickPen?.Dispose();
            _textBrush?.Dispose(); _bgBrush0?.Dispose(); _bgBrush1?.Dispose();
            if (_chPens    != null) foreach (var p in _chPens)    p?.Dispose();
            if (_chBrushes != null) foreach (var b in _chBrushes) b?.Dispose();
            _cursorPenA?.Dispose(); _cursorPenB?.Dispose();
            _cursorBrA?.Dispose();  _cursorBrB?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── File loading ──────────────────────────────────────────────────
    private void OpenFile()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "LA files (*.bin;*.csv)|*.bin;*.csv|Binary (*.bin)|*.bin|CSV (*.csv)|*.csv|All files (*.*)|*.*",
            Title  = "Open Logic Analyser File"
        };
        if (dlg.ShowDialog() == DialogResult.OK) LoadFile(dlg.FileName);
    }

    private void LoadFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".bin") LoadBinary(path);
        else               LoadCsv(path);
    }

    private void LoadBinary(string path)
    {
        const int RECORD_SIZE = 6;  // uint32 timestamp + uint8 pin + uint8 rising

        var rawList    = new List<(double t, int pin, int rising)>();
        var initStates = new int[CHANNELS];

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (fs.Length < 20)
                throw new Exception("File too short to contain a valid header.");

            byte magicA  = br.ReadByte();
            byte magicB  = br.ReadByte();
            if (magicA != 'L' || magicB != 'A')
                throw new Exception("Not a valid LA binary file (bad magic bytes).");

            byte version  = br.ReadByte();
            byte channels = br.ReadByte();
            if (version != 1 && version != 2)
                throw new Exception($"Unsupported file version {version}.");

            br.ReadBytes(8);  // pins[] — not needed for display
            var initRaw = br.ReadBytes(8);
            for (int ch = 0; ch < CHANNELS; ch++)
                initStates[ch] = ch < channels ? initRaw[ch] : 0;

            // Version 2 adds cpu_hz; version 1 timestamps are already µs
            double cyclesPerUs = 1.0;
            if (version == 2)
            {
                uint cpuHz   = br.ReadUInt32();
                cyclesPerUs  = cpuHz / 1_000_000.0;
            }

            int headerSize = version == 2 ? 24 : 20;
            long eventCount = (fs.Length - headerSize) / RECORD_SIZE;

            uint firstRaw  = 0;
            bool haveFirst = false;

            for (long ev = 0; ev < eventCount; ev++)
            {
                uint raw    = br.ReadUInt32();
                byte pin    = br.ReadByte();
                byte rising = br.ReadByte();

                if (!haveFirst) { firstRaw = raw; haveFirst = true; }

                // uint32 subtraction handles wrap-around; divide to get µs
                double relUs = unchecked((uint)(raw - firstRaw)) / cyclesPerUs;

                if (pin < CHANNELS)
                    rawList.Add((relUs, pin, rising != 0 ? 1 : 0));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load binary file:\n{ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (rawList.Count == 0)
        {
            MessageBox.Show("No event records found in binary file.", "Empty file",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _rawEvents     = rawList.ToArray();
        _rawInitStates = initStates;
        _fileName      = Path.GetFileName(path);
        _fileFormat    = "BIN";
        _fileSizeBytes = new FileInfo(path).Length;

        Text = $"Logic Analyser Viewer  —  {_fileName}";
        ExpandAndApply(true);
    }

    private void LoadCsv(string path)
    {
        // Load rows into a temporary flat array, then extract raw events from transitions
        var rowTimes  = new List<double>();
        var rowStates = new List<int[]>();

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("rel_us", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(line)) continue;

                var p = line.Split(',');
                if (p.Length < 2) continue;

                rowTimes.Add(double.Parse(p[0].Trim(), CultureInfo.InvariantCulture));

                var row = new int[CHANNELS];
                for (int ch = 0; ch < CHANNELS; ch++)
                {
                    int col = ch + 1;
                    if (col < p.Length)
                        row[ch] = Math.Clamp(int.Parse(p[col].Trim()) - ch * 2, 0, 1);
                }
                rowStates.Add(row);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load CSV:\n{ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (rowTimes.Count == 0)
        {
            MessageBox.Show("No data rows found.", "Empty file",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Initial states = first row
        var init = new int[CHANNELS];
        for (int ch = 0; ch < CHANNELS; ch++)
            init[ch] = rowStates[0][ch];

        // Extract raw events: rows where any channel changes state
        var rawList = new List<(double t, int pin, int rising)>();
        for (int i = 1; i < rowTimes.Count; i++)
            for (int ch = 0; ch < CHANNELS; ch++)
                if (rowStates[i][ch] != rowStates[i - 1][ch])
                    rawList.Add((rowTimes[i], ch, rowStates[i][ch]));

        if (rawList.Count == 0)
        {
            MessageBox.Show("No transitions found in CSV.", "Empty file",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _rawEvents     = rawList.ToArray();
        _rawInitStates = init;
        _fileName      = Path.GetFileName(path);
        _fileFormat    = "CSV";
        _fileSizeBytes = new FileInfo(path).Length;

        Text = $"Logic Analyser Viewer  —  {_fileName}";
        ExpandAndApply(true);
    }

    // ── Expand + glitch filter ────────────────────────────────────────
    private void ExpandAndApply(bool resetView)
    {
        double minPulse = (double)_glitchFilter.Value;

        // Preserve view window if not resetting (e.g. filter slider changed)
        double prevStart = _viewStart;
        double prevSpan  = _viewEnd - _viewStart;

        // ── Per-channel glitch filter ──
        var filtered = new List<(double t, int pin, int rising)>();
        for (int ch = 0; ch < CHANNELS; ch++)
        {
            // Extract this channel's events in order
            var chEvs = new List<(double t, int rising)>();
            foreach (var ev in _rawEvents)
                if (ev.pin == ch) chEvs.Add((ev.t, ev.rising));

            if (minPulse <= 0)
            {
                foreach (var ev in chEvs) filtered.Add((ev.t, ch, ev.rising));
                continue;
            }

            // Single-pass: skip pairs where pulse width < threshold
            int i = 0;
            while (i < chEvs.Count)
            {
                if (i + 1 < chEvs.Count && chEvs[i + 1].t - chEvs[i].t < minPulse)
                    i += 2;   // glitch pair — drop both
                else
                    filtered.Add((chEvs[i].t, ch, chEvs[i++].rising));
            }
        }

        // Re-sort by time after merging channels
        filtered.Sort((a, b) => a.t.CompareTo(b.t));

        // ── Expand filtered events to step-function ──
        var times  = new List<double>(filtered.Count * 2 + 4);
        var states = new List<int[]>(filtered.Count * 2 + 4);

        var cur = (int[])_rawInitStates.Clone();
        times.Add(0.0);
        states.Add((int[])cur.Clone());

        foreach (var (t, pin, rising) in filtered)
        {
            times.Add(t);  states.Add((int[])cur.Clone());  // before edge
            cur[pin] = rising;
            times.Add(t);  states.Add((int[])cur.Clone());  // after edge
        }

        if (times.Count > 1)
        {
            times.Add(times[^1]);
            states.Add((int[])cur.Clone());
        }

        _times  = times.ToArray();
        _states = new int[_times.Length, CHANNELS];
        for (int i = 0; i < _times.Length; i++)
            for (int ch = 0; ch < CHANNELS; ch++)
                _states[i, ch] = states[i][ch];

        _totalUs = _times.Length > 0 ? _times[^1] : 0;

        if (resetView || prevSpan <= 0)
        {
            _viewStart = 0;
            _viewEnd   = _totalUs;
            _cursorA   = _totalUs * 0.25;
            _cursorB   = _totalUs * 0.75;
        }
        else
        {
            _viewStart = Math.Clamp(prevStart, 0, _totalUs);
            _viewEnd   = Math.Clamp(_viewStart + prevSpan, _viewStart + 1, _totalUs);
        }

        _eventCount = filtered.Count;
        ComputeStats();
        UpdateScrollbar();
        UpdateStatusBar();
        _wavePanel.Invalidate();
    }

    // ── Status bar helpers ────────────────────────────────────────────
    private void ComputeStats()
    {
        // Mark which channels have at least one transition in the filtered data
        _activeCh = new bool[CHANNELS];
        for (int ch = 0; ch < CHANNELS; ch++)
            for (int i = 1; i < _times.Length; i++)
                if (_states[i, ch] != _states[i - 1, ch]) { _activeCh[ch] = true; break; }
    }

    private void UpdateStatusBar()
    {
        if (_times.Length == 0)
        {
            _slFile.Text   = "No file loaded";
            _slEvents.Text = _slSpan.Text = _slRate.Text = _slActive.Text = _slView.Text = _slSize.Text = "";
            return;
        }

        // File
        _slFile.Text = $"  {_fileName}  [{_fileFormat}]";

        // Events
        _slEvents.Text = $"  Events: {_eventCount:N0}";

        // Span
        _slSpan.Text = $"  Span: {FormatTime(_totalUs)}";

        // Rate (events/sec)
        double secs = _totalUs / 1_000_000.0;
        if (secs > 0)
        {
            double rate = _eventCount / secs;
            string rateStr = rate >= 1_000_000 ? $"{rate / 1_000_000.0:0.#}M/s"
                           : rate >= 1_000     ? $"{rate / 1_000.0:0.#}K/s"
                                               : $"{rate:0}/s";
            _slRate.Text = $"  Rate: {rateStr}";
        }
        else _slRate.Text = "";

        // Active channels
        var active = new List<string>();
        for (int ch = 0; ch < CHANNELS; ch++)
            if (_activeCh[ch]) active.Add(CH_NAME[ch]);
        _slActive.Text = active.Count > 0
            ? $"  Active: {string.Join(" ", active)}"
            : "  Active: none";

        // File size
        if (_fileSizeBytes > 0)
        {
            string sizeStr = _fileSizeBytes >= 1_048_576
                ? $"{_fileSizeBytes / 1_048_576.0:0.#} MB"
                : $"{_fileSizeBytes / 1_024.0:0.#} KB";
            _slSize.Text = $"  {sizeStr}";
        }
        else _slSize.Text = "";

        UpdateViewStatus();
        UpdateCursorStatus();
    }

    private void UpdateViewStatus()
    {
        if (_totalUs <= 0) { _slView.Text = ""; return; }
        double span = _viewEnd - _viewStart;
        _slView.Text = $"  View: {FormatTime(span)}";
    }

    private void UpdateCursorStatus()
    {
        if (double.IsNaN(_cursorA) || double.IsNaN(_cursorB)) { _slCursor.Text = ""; return; }
        double dt     = Math.Abs(_cursorB - _cursorA);
        string dtStr  = FormatTime(dt);
        string freqStr = dt > 0 ? $"  ({1_000_000.0 / dt:0.#} Hz)" : "";
        _slCursor.Text = $"  ΔA→B: {dtStr}{freqStr}";
    }

    // ── Paint ─────────────────────────────────────────────────────────
    private void OnPaint(object? sender, PaintEventArgs e)
    {
        var g     = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;

        int pw    = _wavePanel.Width;
        int ph    = _wavePanel.Height;
        int waveH = ph - TIME_H;
        int laneH = LaneH;
        int sigPad = SigPad;

        g.Clear(Color.Black);

        if (_times.Length == 0)
        {
            using var hint = new SolidBrush(Color.FromArgb(70, 70, 70));
            using var font = new Font("Segoe UI", 14);
            const string msg = "Open a file  (File → Open  or  drag & drop  *.bin / *.csv)";
            var sz = g.MeasureString(msg, font);
            g.DrawString(msg, font, hint, (pw - sz.Width) / 2f, (ph - sz.Height) / 2f);
            return;
        }

        // Lane backgrounds
        for (int ch = 0; ch < CHANNELS; ch++)
        {
            int y = WAVE_TOP + ch * laneH;
            if (y >= waveH) break;
            int h = Math.Min(laneH, waveH - y);
            g.FillRectangle(ch % 2 == 0 ? _bgBrush0 : _bgBrush1, LABEL_W, y, pw - LABEL_W, h);
        }

        DrawTimeAxis(g, pw, waveH);

        for (int ch = 0; ch < CHANNELS; ch++)
        {
            int laneTop = WAVE_TOP + ch * laneH;
            if (laneTop >= waveH) break;
            int lh = Math.Min(laneH, waveH - laneTop);

            g.DrawLine(_sepPen, 0, laneTop, pw, laneTop);
            g.DrawString(CH_NAME[ch], _labelFont, _chBrushes[ch],
                         2, laneTop + (lh - _labelFont.Height) / 2);

            DrawChannel(g, ch, laneTop, lh, pw);
        }

        g.DrawLine(_borderPen, 0, waveH, pw, waveH);

        DrawCursors(g, pw, waveH);
    }

    private void DrawChannel(Graphics g, int ch, int laneTop, int laneH, int panelW)
    {
        if (_times.Length < 2) return;

        int waveW = panelW - LABEL_W;
        int sp    = SigPad;
        int yHigh = laneTop + sp;
        int yLow  = laneTop + laneH - sp;

        // Find last index whose time is <= viewStart so segments that span
        // the left edge of the view are included (fixes missing horizontal lines).
        int first = 0;
        for (int i = 1; i < _times.Length; i++)
        {
            if (_times[i] > _viewStart) break;
            first = i;
        }

        // Cap initial capacity at panel width — you can never usefully have more
        // X-unique points than pixels wide, regardless of how many events exist.
        var pts = new List<Point>(Math.Min(_times.Length - first + 2, waveW + 4));

        for (int i = first; i < _times.Length; i++)
        {
            double t = _times[i];
            int    y = _states[i, ch] == 1 ? yHigh : yLow;

            // Clamp first point to left edge of waveform area
            int x = (i == first && t < _viewStart)
                    ? LABEL_W
                    : TimeToX(t, waveW);

            pts.Add(new Point(x, y));

            // Once we pass viewEnd, clamp and stop
            if (t > _viewEnd)
            {
                pts[^1] = new Point(Math.Min(x, LABEL_W + waveW), y);
                break;
            }
        }

        // Extend trailing segment to right edge so the line doesn't stop mid-screen
        if (pts.Count > 0 && pts[^1].X < LABEL_W + waveW)
            pts.Add(new Point(LABEL_W + waveW, pts[^1].Y));

        if (pts.Count < 2) return;

        g.DrawLines(_chPens[ch], pts.ToArray());
    }

    private void DrawTimeAxis(Graphics g, int panelW, int waveH)
    {
        int    waveW    = panelW - LABEL_W;
        double viewSpan = _viewEnd - _viewStart;

        double raw      = viewSpan / 10.0;
        double mag      = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        double norm     = raw / mag;
        double mult     = norm < 2 ? 1 : norm < 5 ? 2 : 5;
        double interval = mult * mag;

        double firstTick = Math.Ceiling(_viewStart / interval) * interval;

        for (double t = firstTick; t <= _viewEnd + interval * 0.01; t += interval)
        {
            int x = TimeToX(t, waveW);
            if (x < LABEL_W || x > panelW) continue;

            g.DrawLine(_gridPen, x, 0, x, waveH);
            g.DrawLine(_tickPen, x, waveH, x, waveH + 5);

            string lbl = FormatTime(t);
            var    sz  = g.MeasureString(lbl, _axisFont);
            g.DrawString(lbl, _axisFont, _textBrush, x - sz.Width / 2f, waveH + 7f);
        }
    }

    private void DrawCursors(Graphics g, int panelW, int waveH)
    {
        if (double.IsNaN(_cursorA) && double.IsNaN(_cursorB)) return;
        int waveW = panelW - LABEL_W;

        void DrawOne(double t, Pen pen, SolidBrush br, string lbl)
        {
            int x = TimeToX(t, waveW);
            if (x < LABEL_W - 1 || x > panelW + 1) return;
            g.DrawLine(pen, x, 0, x, waveH);
            g.DrawString(lbl, _cursorFont, br, x + 2, 2);
        }

        if (!double.IsNaN(_cursorA)) DrawOne(_cursorA, _cursorPenA, _cursorBrA, "A");
        if (!double.IsNaN(_cursorB)) DrawOne(_cursorB, _cursorPenB, _cursorBrB, "B");
    }

    // ── Coordinate helpers ────────────────────────────────────────────
    private int TimeToX(double t, int waveW)
    {
        double frac = (t - _viewStart) / (_viewEnd - _viewStart);
        return LABEL_W + (int)(frac * waveW);
    }

    private double XToTime(int x)
    {
        int    waveW = _wavePanel.Width - LABEL_W;
        double frac  = (double)(x - LABEL_W) / waveW;
        return _viewStart + frac * (_viewEnd - _viewStart);
    }

    private static string FormatTime(double us)
    {
        if (us < 1000)    return $"{us:0.#}µs";
        if (us < 1000000) return $"{us / 1000.0:0.##}ms";
        return                   $"{us / 1000000.0:0.##}s";
    }

    // ── Mouse wheel zoom ──────────────────────────────────────────────
    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_totalUs <= 0) return;

        double tAtCursor = XToTime(e.X);
        double span      = _viewEnd - _viewStart;
        double factor    = e.Delta > 0 ? 0.65 : 1.0 / 0.65;
        double newSpan   = Math.Clamp(span * factor, 1.0, _totalUs * 1.5);

        double ratio = (tAtCursor - _viewStart) / span;
        _viewStart   = tAtCursor - ratio * newSpan;
        _viewEnd     = _viewStart + newSpan;

        ClampView();
        UpdateScrollbar();
        UpdateViewStatus();
        _wavePanel.Invalidate();
    }

    // ── Mouse drag pan ────────────────────────────────────────────────
    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        int waveW = _wavePanel.Width - LABEL_W;
        const int HIT = 6;
        if (!double.IsNaN(_cursorA) && Math.Abs(TimeToX(_cursorA, waveW) - e.X) <= HIT)
        {
            _draggingCursor   = 1;
            _wavePanel.Cursor = Cursors.VSplit;
            return;
        }
        if (!double.IsNaN(_cursorB) && Math.Abs(TimeToX(_cursorB, waveW) - e.X) <= HIT)
        {
            _draggingCursor   = 2;
            _wavePanel.Cursor = Cursors.VSplit;
            return;
        }

        _dragging           = true;
        _dragStartX         = e.X;
        _dragStartViewStart = _viewStart;
        _dragStartViewEnd   = _viewEnd;
        _wavePanel.Cursor   = Cursors.SizeWE;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_totalUs > 0)
            _cursorLabel.Text = $"t = {FormatTime(XToTime(e.X))}";

        // Cursor drag takes priority
        if (_draggingCursor != 0)
        {
            double t = Math.Clamp(XToTime(e.X), 0, _totalUs);
            if (_draggingCursor == 1) _cursorA = t;
            else                      _cursorB = t;
            UpdateCursorStatus();
            _wavePanel.Invalidate();
            return;
        }

        if (!_dragging)
        {
            // Hover: show VSplit cursor when near a cursor line
            if (_totalUs > 0)
            {
                int ww = _wavePanel.Width - LABEL_W;
                bool near = (!double.IsNaN(_cursorA) && Math.Abs(TimeToX(_cursorA, ww) - e.X) <= 6)
                          || (!double.IsNaN(_cursorB) && Math.Abs(TimeToX(_cursorB, ww) - e.X) <= 6);
                _wavePanel.Cursor = near ? Cursors.VSplit : Cursors.Default;
            }
            return;
        }

        int    waveW = _wavePanel.Width - LABEL_W;
        double span  = _dragStartViewEnd - _dragStartViewStart;
        double dt    = -(double)(e.X - _dragStartX) / waveW * span;

        _viewStart = _dragStartViewStart + dt;
        _viewEnd   = _viewStart + span;

        ClampView();
        UpdateScrollbar();
        UpdateViewStatus();
        _wavePanel.Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging         = false;
        _draggingCursor   = 0;
        _wavePanel.Cursor = Cursors.Default;
    }

    // ── Scrollbar pan ─────────────────────────────────────────────────
    private void OnScroll(object? sender, ScrollEventArgs e)
    {
        if (_totalUs <= 0) return;
        double span = _viewEnd - _viewStart;
        _viewStart  = _totalUs * e.NewValue / 10000.0;
        _viewEnd    = _viewStart + span;
        ClampView();
        UpdateViewStatus();
        _wavePanel.Invalidate();
    }

    // ── View helpers ──────────────────────────────────────────────────
    private void ClampView()
    {
        double span = _viewEnd - _viewStart;
        if (_viewStart < 0)          { _viewStart = 0;         _viewEnd = span; }
        if (_viewEnd   > _totalUs)   { _viewEnd   = _totalUs;  _viewStart = Math.Max(0, _viewEnd - span); }
    }

    private void UpdateScrollbar()
    {
        if (_totalUs <= 0) return;
        double span  = _viewEnd - _viewStart;
        int    large = Math.Max(1, (int)(span / _totalUs * 10000));
        _hScroll.Maximum     = 10000 + large;
        _hScroll.LargeChange = large;
        _hScroll.SmallChange = Math.Max(1, large / 10);
        _hScroll.Value       = Math.Clamp((int)(_viewStart / _totalUs * 10000), 0, 10000);
    }
}
