using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GMap.NET;
using GMap.NET.Drawing;
using GMap.NET.WindowsForms;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;
using ZedGraph;

public class ElevationProfilePlugin : Plugin
{
    public override string Name    => "Elevation Profile";
    public override string Version => "0.4";
    public override string Author  => "GitHub Copilot";

    // ── map-overlay panel ────────────────────────────────────────────────────
    private Panel           _panel;
    private Panel           _header;
    private Panel           _leftGrip;
    private Panel           _bottomGrip;
    private ZedGraphControl _zg;
    private Control         _parent;

    // ── background compute ───────────────────────────────────────────────────
    private CancellationTokenSource _cts;
    private readonly object         _updateLock = new object();

    // ── settings tab ─────────────────────────────────────────────────────────
    private TabPage _tabPage;

    // ── persisted settings ───────────────────────────────────────────────────
    private int  _sampleSpacingM = 10;
    private bool _heightLocked   = true;

    // ── change-detection state ───────────────────────────────────────────────
    private double _lastPlaneLat  = double.NaN;
    private double _lastPlaneLon  = double.NaN;
    private double _lastTargetLat = double.NaN;
    private double _lastTargetLon = double.NaN;
    private double _lastPlaneAlt  = double.NaN;
    private double _lastTargetAlt = double.NaN;
    private bool   _firstLoop     = true;

    // ── resize drag state ────────────────────────────────────────────────────
    private bool _resizingW, _resizingH;
    private int  _resizeStartX, _resizeStartY;
    private int  _resizeStartW, _resizeStartH;
    private bool _minAltOnly = false;

    // ── map-overlay min-alt label (shown when _minAltOnly is true) ───────────
    private System.Windows.Forms.Label _minAltMapLabel;

    // ── viewshed map overlay ──────────────────────────────────────────────────
    private Bitmap          _viewshedBitmap;
    private RectLatLng      _viewshedBounds;
    private readonly object _vsLock       = new object();
    private CancellationTokenSource _vsCts;
    private bool            _vsEnabled    = false;
    private int             _vsResDiv     = 3;   // bitmap pixel : screen pixel ratio
    private double          _lastMapZoom  = -1;
    private PointLatLng     _lastMapCenter;
    private GMapControl     _vsGmap;

    // ── terrain gradient overlay ──────────────────────────────────────────────
    private Bitmap          _terBitmap;
    private RectLatLng      _terBounds;
    private readonly object _terLock      = new object();
    private CancellationTokenSource _terCts;
    private bool            _terEnabled   = false;

    // ── GMap overlay layer (index 0 = behind all markers) ────────────────────
    private GMapOverlay          _overlayLayer;
    private BitmapOverlayMarker  _overlayMarker;

    // ── cursor altitude tooltip label ─────────────────────────────────────────
    private System.Windows.Forms.Label _cursorAltLabel;

    // ── SRTM retry flags (auto-refresh when tiles finish downloading) ─────────
    private volatile bool _vsMissingTiles  = false;
    private volatile bool _terMissingTiles = false;

    // ── simulated plane altitude (for elevation planning only) ────────────────
    private bool   _simAltEnabled = false;
    private double _simAltM       = 100.0;

    // ────────────────────────────────────────────────────────────────────────
    public override bool Init()
    {
        loopratehz      = Host.config.GetInt32("ElevationProfile.LoopHz", 2);
        _sampleSpacingM = Host.config.GetInt32("ElevationProfile.SampleSpacingM", 10);
        _heightLocked   = (Host.config["ElevationProfile.HeightLocked"] ?? "True").ToString() != "False";
        _minAltOnly     = (Host.config["ElevationProfile.MinAltOnly"]   ?? "False").ToString() == "True";
        _vsEnabled      = (Host.config["ElevationProfile.ViewshedEnabled"] ?? "False").ToString() == "True";
        _vsResDiv       = Host.config.GetInt32("ElevationProfile.ViewshedResDiv", 3);
        _terEnabled     = (Host.config["ElevationProfile.TerEnabled"] ?? "False").ToString() == "True";
        _simAltEnabled  = (Host.config["ElevationProfile.SimAltEnabled"] ?? "False").ToString() == "True";
        if (double.TryParse(Host.config["ElevationProfile.SimAltM"]?.ToString() ?? "",
                            NumberStyles.Any, CultureInfo.InvariantCulture, out double sa))
            _simAltM = sa;
        // enforce mutual exclusion — terrain takes priority
        if (_terEnabled && _vsEnabled) _vsEnabled = false;
        return true;
    }

    // ────────────────────────────────────────────────────────────────────────
    public override bool Loaded()
    {
        try
        {
            Host.MainForm.BeginInvoke((Action)(() =>
            {
                try
                {
                    _parent = Host.FDGMapControl?.Parent;
                    if (_parent == null) return;

                    // ── outer panel (positioned manually by SetPanelLocation) ─
                    _panel = new Panel()
                    {
                        Name        = "ElevationProfilePanel",
                        BackColor   = Color.WhiteSmoke,
                        BorderStyle = BorderStyle.None
                    };

                    // ── header bar ───────────────────────────────────────────
                    _header = new Panel() { BackColor = Color.SteelBlue };
                    var title = new System.Windows.Forms.Label()
                    {
                        Text      = "Elevation Profile",
                        ForeColor = Color.White,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Dock      = DockStyle.Fill,
                        Padding   = new Padding(4, 0, 0, 0)
                    };
                    var btnHide = new Button()
                    {
                        Text      = "\u00d7",
                        ForeColor = Color.White,
                        Dock      = DockStyle.Right,
                        Width     = 20,
                        FlatStyle = FlatStyle.Flat,
                        Cursor    = Cursors.Hand
                    };
                    btnHide.FlatAppearance.BorderSize = 0;
                    btnHide.Click += (s, e) => ToggleVisible();
                    _header.Controls.Add(title);
                    _header.Controls.Add(btnHide);

                    // ── left edge resize grip (drag left/right to resize width) ─
                    _leftGrip = new Panel() { BackColor = Color.Silver, Cursor = Cursors.SizeWE };
                    _leftGrip.MouseDown += LeftGrip_MouseDown;
                    _leftGrip.MouseMove += LeftGrip_MouseMove;
                    _leftGrip.MouseUp   += LeftGrip_MouseUp;

                    // ── bottom edge resize grip (drag up/down to resize height) ─
                    _bottomGrip = new Panel() { BackColor = Color.Silver, Cursor = Cursors.SizeNS };
                    _bottomGrip.MouseDown += BottomGrip_MouseDown;
                    _bottomGrip.MouseMove += BottomGrip_MouseMove;
                    _bottomGrip.MouseUp   += BottomGrip_MouseUp;

                    // ── ZedGraph chart ───────────────────────────────────────
                    _zg = new ZedGraphControl()
                    {
                        IsEnableZoom      = true,   // rubber-band zoom (left-drag)
                        IsEnableHZoom     = true,   // horizontal zoom only
                        IsEnableVZoom     = false,  // no vertical zoom
                        IsEnableHPan      = true,   // horizontal pan (right-drag)
                        IsEnableVPan      = false,  // no vertical pan
                        IsShowPointValues = true    // tooltip on hover
                    };
                    _zg.GraphPane.Fill       = new Fill(Color.WhiteSmoke);
                    _zg.GraphPane.Chart.Fill = new Fill(Color.White);
                    _zg.BackColor            = Color.WhiteSmoke;
                    // Cursor-centric scroll-wheel zoom (horizontal only)
                    _zg.MouseWheel += (sender, e) =>
                    {
                        try
                        {
                            var zgc = (ZedGraphControl)sender;
                            var gp  = zgc.GraphPane;
                            // convert cursor position to graph X coordinate
                            double cursorX, cursorY;
                            gp.ReverseTransform(e.Location, out cursorX, out cursorY);

                            double xMin = gp.XAxis.Scale.Min;
                            double xMax = gp.XAxis.Scale.Max;
                            double span = xMax - xMin;
                            if (span <= 0) return;

                            // zoom factor: wheel up = zoom in (0.8x span), wheel down = zoom out (1.25x span)
                            double factor = e.Delta > 0 ? 0.8 : 1.25;
                            double newSpan = span * factor;

                            // keep the graph-X value under the cursor fixed
                            double ratio = (cursorX - xMin) / span;
                            double newMin = cursorX - ratio * newSpan;
                            double newMax = newMin + newSpan;

                            gp.XAxis.Scale.MinAuto = false;
                            gp.XAxis.Scale.MaxAuto = false;
                            gp.XAxis.Scale.Min = newMin;
                            gp.XAxis.Scale.Max = newMax;
                            zgc.AxisChange();
                            zgc.Invalidate();
                        }
                        catch { }
                    };

                    _panel.Controls.Add(_header);
                    _panel.Controls.Add(_leftGrip);
                    _panel.Controls.Add(_bottomGrip);
                    _panel.Controls.Add(_zg);
                    _panel.Resize += (s, e) => LayoutPanelContents();

                    SetPanelLocation();
                    LayoutPanelContents();

                    _parent.Controls.Add(_panel);
                    _panel.BringToFront();
                    _parent.Resize += (s, e) => { SetPanelLocation(); _panel.BringToFront(); };

                    // ── map-overlay min-alt label ────────────────────────────
                    var gmap = Host.FDGMapControl;
                    if (gmap != null)
                    {
                        _vsGmap = gmap;
                        gmap.MouseMove  += OnGmapMouseMove;
                        gmap.MouseLeave += OnGmapMouseLeave;

                        // insert behind all existing overlays (plane, waypoints, etc.)
                        _overlayMarker = new BitmapOverlayMarker(this, gmap.Position);
                        _overlayLayer  = new GMapOverlay("ep_bitmap_overlay") { IsVisibile = true };
                        _overlayLayer.Markers.Add(_overlayMarker);
                        gmap.Overlays.Insert(0, _overlayLayer);

                        _cursorAltLabel = new System.Windows.Forms.Label()
                        {
                            AutoSize  = true,
                            Padding   = new Padding(4, 2, 4, 2),
                            BackColor = Color.FromArgb(200, 30, 30, 30),
                            ForeColor = Color.White,
                            Font      = new Font("Segoe UI", 8.5f),
                            Visible   = false
                        };
                        gmap.Controls.Add(_cursorAltLabel);

                        _minAltMapLabel = new System.Windows.Forms.Label()
                        {
                            AutoSize  = false,
                            Width     = 140,
                            Height    = 28,
                            Text      = "",
                            TextAlign = ContentAlignment.MiddleCenter,
                            BackColor = Color.FromArgb(200, 255, 255, 255),
                            ForeColor = Color.LimeGreen,
                            Font      = new Font("Segoe UI", 13, FontStyle.Bold),
                            Visible   = _minAltOnly
                        };
                        gmap.Controls.Add(_minAltMapLabel);
                        Action reposLabel = () =>
                        {
                            try { _minAltMapLabel.Left = gmap.Width - _minAltMapLabel.Width - 50;
                                  _minAltMapLabel.Top  = 25; } catch { }
                        };
                        try { reposLabel(); } catch { }
                        gmap.Resize += (s, e) => { try { reposLabel(); } catch { } };
                    }

                    Host.MainForm.BeginInvoke((Action)ShowPlaceholder);
                }
                catch { }
            }));
        }
        catch { }

        try { CreateSettingsTab(); } catch { }
        return true;
    }

    // ── panel layout (fully manual, no DockStyle conflicts) ──────────────────
    private const int GripW = 3;    // left grip width in px
    private const int GripH = 3;    // bottom grip height in px
    private const int HdrH  = 20;   // header height in px

    private void LayoutPanelContents()
    {
        if (_panel == null) return;
        int w = _panel.ClientSize.Width;
        int h = _panel.ClientSize.Height;

        if (_header     != null) _header.Bounds     = new Rectangle(0,     0,          w,          HdrH);
        if (_leftGrip   != null) _leftGrip.Bounds   = new Rectangle(0,     HdrH,       GripW,      h - HdrH - GripH);
        if (_bottomGrip != null) _bottomGrip.Bounds = new Rectangle(0,     h - GripH,  w,          GripH);
        if (_zg         != null) _zg.Bounds         = new Rectangle(GripW, HdrH,       w - GripW,  h - HdrH - GripH);
    }

    private int GetZoomOffset()
    {
        var tb = _parent?.Controls.OfType<TrackBar>().FirstOrDefault();
        return (tb?.Width ?? 40) + 10;
    }

    private void SetPanelLocation()
    {
        if (_parent == null || _panel == null) return;
        int topOffset  = 30;
        int width      = Math.Max(150, Host.config.GetInt32("ElevationProfile.Width", 350));
        int zoomOffset = GetZoomOffset();

        _panel.Width  = width;
        _panel.Height = _heightLocked
            ? Math.Max(100, _parent.Height - topOffset - 10)
            : Math.Max(100, Host.config.GetInt32("ElevationProfile.Height", _parent.Height - topOffset - 10));

        _panel.Location = new Point(_parent.Width - width - zoomOffset, topOffset);
        LayoutPanelContents();
    }

    private void ToggleVisible()
    {
        if (_panel == null) return;
        _panel.Visible = !_panel.Visible;
        Host.config["ElevationProfile.Visible"] = _panel.Visible.ToString();
        try { Host.config.Save(); } catch { }
    }

    // ── left resize grip (changes panel width) ────────────────────────────────
    private void LeftGrip_MouseDown(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _resizingW = true;
        _resizeStartX = Cursor.Position.X;
        _resizeStartW = _panel.Width;
    }
    private void LeftGrip_MouseMove(object s, MouseEventArgs e)
    {
        if (!_resizingW) return;
        int newW = Math.Max(150, _resizeStartW + (_resizeStartX - Cursor.Position.X));
        _panel.Width    = newW;
        _panel.Left     = _parent.Width - newW - GetZoomOffset();
        LayoutPanelContents();
    }
    private void LeftGrip_MouseUp(object s, MouseEventArgs e)
    {
        if (!_resizingW) return;
        _resizingW = false;
        Host.config["ElevationProfile.Width"] = _panel.Width.ToString();
        try { Host.config.Save(); } catch { }
    }

    // ── bottom resize grip (changes panel height) ─────────────────────────────
    private void BottomGrip_MouseDown(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _resizingH = true;
        _resizeStartY = Cursor.Position.Y;
        _resizeStartH = _panel.Height;
    }
    private void BottomGrip_MouseMove(object s, MouseEventArgs e)
    {
        if (!_resizingH) return;
        int newH = Math.Max(100, _resizeStartH + (Cursor.Position.Y - _resizeStartY));
        _panel.Height = newH;
        LayoutPanelContents();
    }
    private void BottomGrip_MouseUp(object s, MouseEventArgs e)
    {
        if (!_resizingH) return;
        _resizingH    = false;
        _heightLocked = false;
        Host.config["ElevationProfile.HeightLocked"] = "False";
        Host.config["ElevationProfile.Height"]       = _panel.Height.ToString();
        try { Host.config.Save(); } catch { }
    }

    // ── settings tab ─────────────────────────────────────────────────────────
    private void CreateSettingsTab()
    {
        Action createTab = () =>
        {
            try
            {
                if (Host?.MainForm?.FlightData == null) return;

                _tabPage = new TabPage()
                {
                    Text       = "ELEV PROFILE",
                    Name       = "tabElevationProfile",
                    AutoScroll = true
                };

                int x1 = 6, x2 = 172, rowH = 28, y = 8;

                // ── sample spacing ───────────────────────────────────────────
                _tabPage.Controls.Add(MkLabel("Sample spacing (m):", x1, y));
                var nudSpacing = new NumericUpDown()
                    { Left = x2, Top = y, Width = 80, Minimum = 2, Maximum = 500, Value = _sampleSpacingM };
                nudSpacing.ValueChanged += (s, e) =>
                {
                    _sampleSpacingM = (int)nudSpacing.Value;
                    Host.config["ElevationProfile.SampleSpacingM"] = _sampleSpacingM.ToString();
                    try { Host.config.Save(); } catch { }
                    _firstLoop = true;
                };
                _tabPage.Controls.Add(nudSpacing);
                y += rowH;

                // ── update rate ──────────────────────────────────────────────
                _tabPage.Controls.Add(MkLabel("Update rate (Hz):", x1, y));
                var nudHz = new NumericUpDown()
                    { Left = x2, Top = y, Width = 80, Minimum = 1, Maximum = 10, Value = (int)loopratehz };
                nudHz.ValueChanged += (s, e) =>
                {
                    loopratehz = (int)nudHz.Value;
                    Host.config["ElevationProfile.LoopHz"] = loopratehz.ToString();
                    try { Host.config.Save(); } catch { }
                };
                _tabPage.Controls.Add(nudHz);
                y += rowH;

                // ── panel width ──────────────────────────────────────────────
                _tabPage.Controls.Add(MkLabel("Panel width (px):", x1, y));
                var nudWidth = new NumericUpDown()
                    { Left = x2, Top = y, Width = 80, Minimum = 150, Maximum = 1200, Increment = 10,
                      Value = Host.config.GetInt32("ElevationProfile.Width", 350) };
                nudWidth.ValueChanged += (s, e) =>
                {
                    try
                    {
                        int newW = (int)nudWidth.Value;
                        Host.config["ElevationProfile.Width"] = newW.ToString();
                        try { Host.config.Save(); } catch { }
                        if (_panel != null && _parent != null)
                        {
                            _panel.Width = newW;
                            _panel.Left  = _parent.Width - newW - GetZoomOffset();
                            LayoutPanelContents();
                        }
                    }
                    catch { }
                };
                _tabPage.Controls.Add(nudWidth);
                y += rowH;

                // ── lock height ──────────────────────────────────────────────
                var chkLock = new CheckBox()
                    { Left = x1, Top = y, Width = 240, Text = "Lock height to window", Checked = _heightLocked };
                chkLock.CheckedChanged += (s, e) =>
                {
                    _heightLocked = chkLock.Checked;
                    Host.config["ElevationProfile.HeightLocked"] = _heightLocked.ToString();
                    try { Host.config.Save(); } catch { }
                    if (_panel != null) SetPanelLocation();
                };
                _tabPage.Controls.Add(chkLock);
                y += rowH + 6;

                // ── min alt only ─────────────────────────────────────────────
                var chkMinAlt = new CheckBox()
                    { Left = x1, Top = y, Width = 240, Text = "Show min safe AGL only", Checked = _minAltOnly };
                chkMinAlt.CheckedChanged += (s, e) =>
                {
                    _minAltOnly = chkMinAlt.Checked;
                    Host.config["ElevationProfile.MinAltOnly"] = _minAltOnly.ToString();
                    try { Host.config.Save(); } catch { }
                    _firstLoop  = true;
                };
                _tabPage.Controls.Add(chkMinAlt);
                y += rowH + 6;
                var btnRefresh = new Button() { Left = x1,       Top = y, Width = 110, Height = 26, Text = "Force Refresh" };
                btnRefresh.Click += (s, e) =>
                {
                    _firstLoop = true;
                    lock (_vsLock) { _viewshedBitmap?.Dispose(); _viewshedBitmap = null; }
                    lock (_terLock) { _terBitmap?.Dispose(); _terBitmap = null; }
                };
                _tabPage.Controls.Add(btnRefresh);

                var btnToggle = new Button() { Left = x1 + 118, Top = y, Width = 110, Height = 26, Text = "Show / Hide" };
                btnToggle.Click += (s, e) => ToggleVisible();
                _tabPage.Controls.Add(btnToggle);
                y += rowH + 10;

                // ── map overlay mode (radio buttons, mutually exclusive) ────
                _tabPage.Controls.Add(new System.Windows.Forms.Label()
                {
                    Left = x1, Top = y, Width = 300, Height = 18,
                    Text = "Map overlay mode:", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
                });
                y += 20;

                var rbNone = new RadioButton()
                    { Left = x1, Top = y, Width = 240, Text = "None",
                      Checked = !_terEnabled && !_vsEnabled };
                _tabPage.Controls.Add(rbNone);
                y += rowH;

                var rbTer = new RadioButton()
                    { Left = x1, Top = y, Width = 240, Text = "Elevation gradient map",
                      Checked = _terEnabled };
                _tabPage.Controls.Add(rbTer);
                y += rowH;

                var rbVs = new RadioButton()
                    { Left = x1, Top = y, Width = 240, Text = "Viewshed (line-of-sight)",
                      Checked = _vsEnabled };
                _tabPage.Controls.Add(rbVs);
                y += rowH + 6;

                Action applyOverlayMode = () =>
                {
                    bool newTer = rbTer.Checked;
                    bool newVs  = rbVs.Checked;

                    if (!newTer && _terEnabled)
                    {
                        _terEnabled = false;
                        Host.config["ElevationProfile.TerEnabled"] = "False";
                        lock (_terLock) { _terBitmap?.Dispose(); _terBitmap = null; }
                    }
                    else if (newTer && !_terEnabled)
                    {
                        _terEnabled = true;
                        Host.config["ElevationProfile.TerEnabled"] = "True";
                        _firstLoop  = true;
                    }

                    if (!newVs && _vsEnabled)
                    {
                        _vsEnabled = false;
                        Host.config["ElevationProfile.ViewshedEnabled"] = "False";
                        lock (_vsLock) { _viewshedBitmap?.Dispose(); _viewshedBitmap = null; }
                    }
                    else if (newVs && !_vsEnabled)
                    {
                        _vsEnabled = true;
                        Host.config["ElevationProfile.ViewshedEnabled"] = "True";
                        _firstLoop  = true;
                    }

                    try { Host.config.Save(); } catch { }
                    try { _vsGmap?.Invoke((Action)(() => _vsGmap?.Invalidate())); } catch { }
                };

                rbNone.CheckedChanged += (s, e) => { if (rbNone.Checked) applyOverlayMode(); };
                rbTer .CheckedChanged += (s, e) => { if (rbTer.Checked)  applyOverlayMode(); };
                rbVs  .CheckedChanged += (s, e) => { if (rbVs.Checked)   applyOverlayMode(); };

                // ── overlay resolution divisor ───────────────────────────────
                _tabPage.Controls.Add(MkLabel("Overlay resolution:", x1, y));
                var nudResDiv = new NumericUpDown()
                    { Left = x2, Top = y, Width = 80, Minimum = 1, Maximum = 8, Value = _vsResDiv };
                nudResDiv.ValueChanged += (s, e) =>
                {
                    _vsResDiv = (int)nudResDiv.Value;
                    Host.config["ElevationProfile.ViewshedResDiv"] = _vsResDiv.ToString();
                    try { Host.config.Save(); } catch { }
                    if (_vsEnabled) { lock (_vsLock) { _viewshedBitmap?.Dispose(); _viewshedBitmap = null; } _firstLoop = true; }
                    if (_terEnabled) { lock (_terLock) { _terBitmap?.Dispose(); _terBitmap = null; } _firstLoop = true; }
                };
                _tabPage.Controls.Add(nudResDiv);
                y += rowH;

                _tabPage.Controls.Add(new System.Windows.Forms.Label()
                {
                    Left = x1, Top = y, Width = 320, Height = 20,
                    Text = "(1=finest but slowest, 8=coarsest but fast)",
                    ForeColor = Color.DimGray, Font = new Font("Segoe UI", 7.5f)
                });
                y += rowH + 4;

                // ── simulated plane altitude ─────────────────────────────────
                _tabPage.Controls.Add(new System.Windows.Forms.Label()
                {
                    Left = x1, Top = y, Width = 300, Height = 18,
                    Text = "Simulated plane altitude:", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
                });
                y += 20;

                var chkSimAlt = new CheckBox()
                    { Left = x1, Top = y, Width = 240, Text = "Use simulated altitude (AGL, metres)",
                      Checked = _simAltEnabled };
                _tabPage.Controls.Add(chkSimAlt);
                y += rowH;

                _tabPage.Controls.Add(MkLabel("Altitude (m AGL):", x1, y));
                var nudSimAlt = new NumericUpDown()
                    { Left = x2, Top = y, Width = 90, Minimum = 0, Maximum = 10000,
                      DecimalPlaces = 1, Value = (decimal)Math.Min(10000, Math.Max(0, _simAltM)) };
                nudSimAlt.ValueChanged += (s, e) =>
                {
                    _simAltM = (double)nudSimAlt.Value;
                    Host.config["ElevationProfile.SimAltM"] = _simAltM.ToString(CultureInfo.InvariantCulture);
                    try { Host.config.Save(); } catch { }
                    if (_simAltEnabled && _vsEnabled) { _vsMissingTiles = false; _firstLoop = true; }
                };
                _tabPage.Controls.Add(nudSimAlt);
                y += rowH + 4;

                chkSimAlt.CheckedChanged += (s, e) =>
                {
                    _simAltEnabled = chkSimAlt.Checked;
                    Host.config["ElevationProfile.SimAltEnabled"] = _simAltEnabled.ToString();
                    try { Host.config.Save(); } catch { }
                    nudSimAlt.Enabled = _simAltEnabled;
                    if (_vsEnabled) { lock (_vsLock) { _viewshedBitmap?.Dispose(); _viewshedBitmap = null; } _firstLoop = true; }
                };
                nudSimAlt.Enabled = _simAltEnabled;

                // ── hint text ────────────────────────────────────────────────
                _tabPage.Controls.Add(new System.Windows.Forms.Label()
                {
                    Left      = x1, Top = y, Width = 320, Height = 36,
                    Text      = "Drag left edge \u2194 to resize width.\nDrag bottom edge \u2195 to resize height.",
                    ForeColor = Color.DimGray
                });

                Host.MainForm.FlightData.TabListOriginal.Add(_tabPage);
                var tc = Host.MainForm.FlightData.tabControlactions;
                if (!tc.TabPages.Contains(_tabPage))
                    tc.TabPages.Insert(Math.Min(6, tc.TabPages.Count), _tabPage);

                try { ThemeManager.ApplyThemeTo(_tabPage); } catch { }
            }
            catch { }
        };

        var fd = MissionPlanner.GCSViews.FlightData.instance;
        if (fd != null)
        {
            try { fd.BeginInvoke(createTab); }
            catch { createTab(); }
        }
        else
        {
            createTab();
        }
    }

    private System.Windows.Forms.Label MkLabel(string text, int x, int y)
    {
        return new System.Windows.Forms.Label()
            { Left = x, Top = y, Width = 162, Text = text, TextAlign = ContentAlignment.MiddleLeft };
    }

    // ── Loop ─────────────────────────────────────────────────────────────────
    public override bool Loop()
    {
        try
        {
            var cfg = Host.config;
            double pLat = double.NaN, pLon = double.NaN, tLat = double.NaN, tLon = double.NaN;

            double pAlt = double.NaN, tAlt = double.NaN;
            try
            {
                if (cfg["Companion_plane_lat"]  != null) double.TryParse(cfg["Companion_plane_lat"].ToString(),  NumberStyles.Any, CultureInfo.InvariantCulture, out pLat);
                if (cfg["Companion_plane_lon"]  != null) double.TryParse(cfg["Companion_plane_lon"].ToString(),  NumberStyles.Any, CultureInfo.InvariantCulture, out pLon);
                if (cfg["Companion_target_lat"] != null) double.TryParse(cfg["Companion_target_lat"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tLat);
                if (cfg["Companion_target_lon"] != null) double.TryParse(cfg["Companion_target_lon"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tLon);
                // altitudes
                if (cfg["Companion_plane_alt"]  != null) double.TryParse(cfg["Companion_plane_alt"].ToString(),  NumberStyles.Any, CultureInfo.InvariantCulture, out pAlt);
                if (cfg["Companion_target_alt"] != null) double.TryParse(cfg["Companion_target_alt"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tAlt);
            }
            catch { }

            // fall back to autopilot telemetry
            if (double.IsNaN(pLat) || double.IsNaN(pLon))
            {
                var loc = Host.cs?.Location;
                if (loc != null) { pLat = loc.Lat; pLon = loc.Lng; }
            }
            if (double.IsNaN(pAlt))
            {
                var loc = Host.cs?.Location;
                if (loc != null) pAlt = loc.Alt;
            }
            if (double.IsNaN(tLat) || double.IsNaN(tLon))
            {
                var tl = Host.cs?.TargetLocation;
                if (tl != null) { tLat = tl.Lat; tLon = tl.Lng; }
            }
            if (double.IsNaN(tAlt)) tAlt = 0;

            // map pan/zoom detection — runs regardless of overlay mode
            bool mapChanged = false;
            if ((_vsEnabled || _terEnabled) && _vsGmap != null)
            {
                double curZoom   = _vsGmap.Zoom;
                var    curCenter = _vsGmap.Position;
                if (Math.Abs(curZoom - _lastMapZoom) > 0.01
                    || Math.Abs(curCenter.Lat - _lastMapCenter.Lat) > 1e-6
                    || Math.Abs(curCenter.Lng - _lastMapCenter.Lng) > 1e-6)
                {
                    mapChanged     = true;
                    _lastMapZoom   = curZoom;
                    _lastMapCenter = curCenter;
                }
            }

            if (double.IsNaN(pLat) || double.IsNaN(tLat))
            {
                // no plane position — still refresh terrain on pan/zoom
                if (_terEnabled && (mapChanged || _terMissingTiles)) { _terMissingTiles = false; ScheduleTerUpdate(); }
                return true;
            }

            double dpLat = pLat - _lastPlaneLat,  dpLon = pLon - _lastPlaneLon;
            double dtLat = tLat - _lastTargetLat, dtLon = tLon - _lastTargetLon;
            double dAlt  = Math.Abs(pAlt - _lastPlaneAlt) + Math.Abs(tAlt - _lastTargetAlt);
            bool changed = _firstLoop
                || (dpLat * dpLat + dpLon * dpLon > 1e-8)
                || (dtLat * dtLat + dtLon * dtLon > 1e-8)
                || dAlt > 0.5;

            if (changed)
            {
                _firstLoop     = false;
                _lastPlaneLat  = pLat; _lastPlaneLon  = pLon;
                _lastTargetLat = tLat; _lastTargetLon = tLon;
                _lastPlaneAlt  = pAlt; _lastTargetAlt = tAlt;
                ScheduleUpdate(new PointLatLngAlt(pLat, pLon, 0),
                               new PointLatLngAlt(tLat, tLon, 0),
                               pAlt, tAlt);
            }

            if (_vsEnabled  && (changed || mapChanged || _vsMissingTiles))  { _vsMissingTiles  = false; ScheduleViewshedUpdate(new PointLatLng(pLat, pLon), _simAltEnabled ? _simAltM : pAlt); }
            if (_terEnabled && (changed || mapChanged || _terMissingTiles)) { _terMissingTiles = false; ScheduleTerUpdate(); }
        }
        catch { }
        return true;
    }

    // ── compute and plot ──────────────────────────────────────────────────────
    private void ScheduleUpdate(PointLatLngAlt start, PointLatLngAlt end, double planeAlt, double targetAlt)
    {
        lock (_updateLock)
        {
            var prev = _cts;
            _cts = new CancellationTokenSource();
            try { prev?.Cancel(); prev?.Dispose(); } catch { }

            if (start.Lat == 0 && start.Lng == 0 && end.Lat == 0 && end.Lng == 0)
            {
                Host.MainForm.BeginInvoke((Action)ShowPlaceholder);
                return;
            }

            Task.Run(() => ComputeAndPlotAsync(start, end, planeAlt, targetAlt, _cts.Token), _cts.Token);
        }
    }

    // ── viewshed scheduling ───────────────────────────────────────────────────
    private void ScheduleViewshedUpdate(PointLatLng planePos, double planeAltAGL)
    {
        var prev = _vsCts;
        _vsCts = new CancellationTokenSource();
        try { prev?.Cancel(); prev?.Dispose(); } catch { }
        Task.Run(() => ComputeViewshed(planePos, planeAltAGL, _vsCts.Token), _vsCts.Token);
    }

    private void ComputeViewshed(PointLatLng planePos, double planeAltAGL, CancellationToken token)
    {
        try
        {
            var gmap = _vsGmap;
            if (gmap == null || gmap.IsDisposed) return;

            int scrW = 0, scrH = 0;
            RectLatLng viewArea = default;
            gmap.Invoke((Action)(() =>
            {
                scrW     = gmap.Width;
                scrH     = gmap.Height;
                viewArea = gmap.ViewArea;
            }));

            if (scrW < 10 || scrH < 10) return;
            if (token.IsCancellationRequested) return;

            int div  = Math.Max(1, _vsResDiv);
            int bmpW = Math.Max(4, scrW / div);
            int bmpH = Math.Max(4, scrH / div);

            // ── compute lat/lng for every grid cell via Mercator math ────────
            // (avoids blocking the UI thread for a big coordinate batch)
            double topLat   = viewArea.LocationTopLeft.Lat;
            double botLat   = viewArea.LocationRightBottom.Lat;
            double leftLng  = viewArea.LocationTopLeft.Lng;
            double rightLng = viewArea.LocationRightBottom.Lng;

            double mercTop = Math.Log(Math.Tan(Math.PI / 4.0 + topLat * Math.PI / 180.0 / 2.0));
            double mercBot = Math.Log(Math.Tan(Math.PI / 4.0 + botLat * Math.PI / 180.0 / 2.0));

            var latRow = new double[bmpH];
            var lngCol = new double[bmpW];
            for (int py = 0; py < bmpH; py++)
            {
                double t  = (py + 0.5) / bmpH;
                double my = mercTop + t * (mercBot - mercTop);
                latRow[py] = (2.0 * Math.Atan(Math.Exp(my)) - Math.PI / 2.0) * 180.0 / Math.PI;
            }
            for (int px = 0; px < bmpW; px++)
                lngCol[px] = leftLng + (px + 0.5) / bmpW * (rightLng - leftLng);

            if (token.IsCancellationRequested) return;

            // ── single-pass SRTM fetch for all grid cells ────────────────────
            var altGrid = new double[bmpW * bmpH];    // NaN = no data
            bool missingVsTiles = false;
            for (int py = 0; py < bmpH; py++)
            {
                if (token.IsCancellationRequested) return;
                for (int px = 0; px < bmpW; px++)
                {
                    var r = srtm.getAltitude(latRow[py], lngCol[px]);
                    if (r.currenttype == srtm.tiletype.invalid) missingVsTiles = true;
                    altGrid[py * bmpW + px] = r.currenttype == srtm.tiletype.valid
                        ? r.alt : double.NaN;
                }
            }

            if (token.IsCancellationRequested) return;

            // ── locate plane in bitmap coords (unclamped — plane may be off-screen) ──
            double planeMercY = Math.Log(Math.Tan(Math.PI / 4.0 + planePos.Lat * Math.PI / 180.0 / 2.0));
            double tPY  = (planeMercY - mercTop) / (mercBot - mercTop);
            double tPX  = (planePos.Lng - leftLng) / (rightLng - leftLng);
            // floating-point bitmap coords — may be outside [0, bmpW/bmpH)
            double ppxF = tPX * bmpW;
            double ppyF = tPY * bmpH;

            // fetch terrain under the plane directly from SRTM (not from altGrid,
            // since the plane may be off-screen)
            var planeSRTM = srtm.getAltitude(planePos.Lat, planePos.Lng);
            if (planeSRTM.currenttype == srtm.tiletype.invalid) missingVsTiles = true;
            double planeTerrASL = planeSRTM.currenttype == srtm.tiletype.valid ? planeSRTM.alt : 0;
            double planeAltASL  = planeTerrASL + planeAltAGL;

            double mPerPxLat = (viewArea.HeightLat * 111320.0) / scrH;
            double maxDist   = Math.Max(500.0, mPerPxLat * Math.Max(scrW, scrH) * 0.8);
            double cosLat    = Math.Cos(planePos.Lat * Math.PI / 180.0);

            var bmpData = new byte[bmpW * bmpH * 4]; // BGRA

            for (int py = 0; py < bmpH; py++)
            {
                if (token.IsCancellationRequested) return;
                for (int px = 0; px < bmpW; px++)
                {
                    int    idx       = (py * bmpW + px) * 4;
                    double targetAlt = altGrid[py * bmpW + px];

                    if (double.IsNaN(targetAlt)) continue; // transparent

                    // distance from plane (metres)
                    double dLat = (latRow[py] - planePos.Lat) * 111320.0;
                    double dLon = (lngCol[px] - planePos.Lng) * 111320.0 * cosLat;
                    double dist = Math.Sqrt(dLat * dLat + dLon * dLon);

                    // ── LOS via Bresenham walk on altGrid ─────────────────────
                    // uses unclamped float plane coords; skips cells outside bitmap
                    bool blocked = false;
                    double dpxF  = px - ppxF;
                    double dpyF  = py - ppyF;
                    int  steps   = Math.Max(1, Math.Max((int)Math.Abs(dpxF), (int)Math.Abs(dpyF)));
                    for (int s = 1; s < steps && !blocked; s++)
                    {
                        double t  = (double)s / steps;
                        int    ix = (int)Math.Round(ppxF + t * dpxF);
                        int    iy = (int)Math.Round(ppyF + t * dpyF);
                        if (ix < 0 || ix >= bmpW || iy < 0 || iy >= bmpH) continue;
                        double iAlt = altGrid[iy * bmpW + ix];
                        if (double.IsNaN(iAlt)) continue;
                        double losAlt = planeAltASL + t * (targetAlt - planeAltASL);
                        if (iAlt > losAlt + 0.5) blocked = true;
                    }

                    if (blocked)
                    {
                        bmpData[idx]   = 50;   // B
                        bmpData[idx+1] = 40;   // G
                        bmpData[idx+2] = 220;  // R
                        bmpData[idx+3] = 120;  // A
                    }
                    else
                    {
                        double tDist   = Math.Min(1.0, dist / maxDist);
                        bmpData[idx]   = 0;                          // B
                        bmpData[idx+1] = 210;                        // G
                        bmpData[idx+2] = (byte)(int)(tDist * 200);  // R
                        bmpData[idx+3] = (byte)(int)(160 - tDist * 60); // A
                    }
                }
            }

            if (token.IsCancellationRequested) return;

            var newBmp = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppArgb);
            var bd = newBmp.LockBits(new Rectangle(0, 0, bmpW, bmpH),
                                     ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bd.Scan0, bmpData.Length);
            newBmp.UnlockBits(bd);

            Bitmap oldBmp;
            lock (_vsLock)
            {
                oldBmp          = _viewshedBitmap;
                _viewshedBitmap = newBmp;
                _viewshedBounds = viewArea;
            }
            oldBmp?.Dispose();
            if (missingVsTiles) _vsMissingTiles = true;

            try { gmap.Invoke((Action)(() => { if (_overlayMarker != null) _overlayMarker.Position = gmap.Position; gmap.Invalidate(); })); } catch { }
        }
        catch { }
    }

    private void OnGmapMouseMove(object sender, MouseEventArgs e)
    {
        if (!(_vsEnabled || _terEnabled) || _vsGmap == null || _cursorAltLabel == null)
        {
            if (_cursorAltLabel != null && _cursorAltLabel.Visible)
                _cursorAltLabel.Visible = false;
            return;
        }
        try
        {
            var gm  = _vsGmap;
            var pos = gm.FromLocalToLatLng(e.X, e.Y);
            var r   = srtm.getAltitude(pos.Lat, pos.Lng);
            string text = r.currenttype == srtm.tiletype.valid
                ? $"{r.alt:0} m"
                : "-- m";

            _cursorAltLabel.Text = text;

            // position offset from cursor, clamped inside map
            int lx = e.X + 14;
            int ly = e.Y - 22;
            if (lx + _cursorAltLabel.Width  > gm.Width)  lx = e.X - _cursorAltLabel.Width  - 4;
            if (ly < 0)                                   ly = e.Y + 14;

            _cursorAltLabel.Left    = lx;
            _cursorAltLabel.Top     = ly;
            _cursorAltLabel.Visible = true;
            _cursorAltLabel.BringToFront();
        }
        catch { }
    }

    private void OnGmapMouseLeave(object sender, EventArgs e)
    {
        try { if (_cursorAltLabel != null) _cursorAltLabel.Visible = false; } catch { }
    }

    // ── bitmap overlay marker (renders behind all GMap marker overlays) ────────
    private class BitmapOverlayMarker : GMapMarker
    {
        private readonly ElevationProfilePlugin _p;
        public BitmapOverlayMarker(ElevationProfilePlugin p, PointLatLng pos) : base(pos)
        {
            _p = p;
            IsVisible = true;
        }

        public override void OnRender(IGraphics g)
        {
            var gm = _p._vsGmap;
            if (gm == null || gm.IsDisposed) return;

            // GMap pre-applies renderOffset to the Graphics context before calling OnRender,
            // but FromLatLngToLocal already includes renderOffset (absolute screen coords).
            // Reset to identity so both use the same coordinate space, then restore.
            System.Drawing.Drawing2D.Matrix savedMatrix    = null;
            System.Drawing.Drawing2D.Matrix identityMatrix  = null;
            try
            {
                savedMatrix   = g.Transform;
                identityMatrix = new System.Drawing.Drawing2D.Matrix();
                g.Transform   = identityMatrix;
            }
            catch { }

            // terrain gradient layer (drawn first, behind viewshed)
            if (_p._terEnabled)
            {
                Bitmap bmp; RectLatLng bounds;
                lock (_p._terLock) { bmp = _p._terBitmap; bounds = _p._terBounds; }
                DrawBitmap(g, gm, bmp, bounds);
            }

            // viewshed layer on top
            if (_p._vsEnabled)
            {
                Bitmap bmp; RectLatLng bounds;
                lock (_p._vsLock) { bmp = _p._viewshedBitmap; bounds = _p._viewshedBounds; }
                DrawBitmap(g, gm, bmp, bounds);
            }

            try
            {
                if (savedMatrix != null) g.Transform = savedMatrix;
                savedMatrix?.Dispose();
                identityMatrix?.Dispose();
            }
            catch { }
        }

        private static void DrawBitmap(IGraphics g, GMapControl gm, Bitmap bmp, RectLatLng bounds)
        {
            if (bmp == null) return;
            try
            {
                var tl = gm.FromLatLngToLocal(bounds.LocationTopLeft);
                var br = gm.FromLatLngToLocal(bounds.LocationRightBottom);
                int x = (int)tl.X, y = (int)tl.Y;
                int w = (int)(br.X - tl.X), h = (int)(br.Y - tl.Y);
                if (w > 0 && h > 0)
                    g.DrawImage(bmp, new Rectangle(x, y, w, h));
            }
            catch { }
        }
    }

    // ── terrain gradient scheduling & compute ─────────────────────────────────
    private void ScheduleTerUpdate()
    {
        var prev = _terCts;
        _terCts = new CancellationTokenSource();
        try { prev?.Cancel(); prev?.Dispose(); } catch { }
        Task.Run(() => ComputeTerrain(_terCts.Token), _terCts.Token);
    }

    private void ComputeTerrain(CancellationToken token)
    {
        try
        {
            var gmap = _vsGmap;
            if (gmap == null || gmap.IsDisposed) return;

            int scrW = 0, scrH = 0;
            RectLatLng viewArea = default;
            gmap.Invoke((Action)(() =>
            {
                scrW     = gmap.Width;
                scrH     = gmap.Height;
                viewArea = gmap.ViewArea;
            }));
            if (scrW < 10 || scrH < 10 || token.IsCancellationRequested) return;

            int div  = Math.Max(1, _vsResDiv);
            int bmpW = Math.Max(4, scrW / div);
            int bmpH = Math.Max(4, scrH / div);

            // compute lat/lng via Mercator math — no UI-thread invoke needed
            double topLat   = viewArea.LocationTopLeft.Lat;
            double botLat   = viewArea.LocationRightBottom.Lat;
            double leftLng  = viewArea.LocationTopLeft.Lng;
            double rightLng = viewArea.LocationRightBottom.Lng;
            double mercTopT = Math.Log(Math.Tan(Math.PI / 4.0 + topLat * Math.PI / 180.0 / 2.0));
            double mercBotT = Math.Log(Math.Tan(Math.PI / 4.0 + botLat * Math.PI / 180.0 / 2.0));
            var latRowT = new double[bmpH];
            var lngColT = new double[bmpW];
            for (int py = 0; py < bmpH; py++)
            {
                double t  = (py + 0.5) / bmpH;
                double my = mercTopT + t * (mercBotT - mercTopT);
                latRowT[py] = (2.0 * Math.Atan(Math.Exp(my)) - Math.PI / 2.0) * 180.0 / Math.PI;
            }
            for (int px = 0; px < bmpW; px++)
                lngColT[px] = leftLng + (px + 0.5) / bmpW * (rightLng - leftLng);

            if (token.IsCancellationRequested) return;

            // collect altitudes and find min/max
            var terrAlts = new double[bmpW * bmpH];
            bool missingTerTiles = false;
            double minAlt = double.MaxValue, maxAlt = double.MinValue;
            for (int py = 0; py < bmpH; py++)
            {
                if (token.IsCancellationRequested) return;
                for (int px = 0; px < bmpW; px++)
                {
                    var r = srtm.getAltitude(latRowT[py], lngColT[px]);
                    if (r.currenttype == srtm.tiletype.valid)
                    {
                        terrAlts[py * bmpW + px] = r.alt;
                        if (r.alt < minAlt) minAlt = r.alt;
                        if (r.alt > maxAlt) maxAlt = r.alt;
                    }
                    else
                    {
                        if (r.currenttype == srtm.tiletype.invalid) missingTerTiles = true;
                        terrAlts[py * bmpW + px] = double.NaN;
                    }
                }
            }
            if (token.IsCancellationRequested) return;

            double range = maxAlt - minAlt;
            if (range < 1.0) range = 1.0;

            // second pass: assign colors (blue→cyan→green→yellow→red)
            var bmpData = new byte[bmpW * bmpH * 4];
            for (int i = 0; i < bmpW * bmpH; i++)
            {
                int idx = i * 4;
                if (double.IsNaN(terrAlts[i])) { bmpData[idx+3] = 0; continue; }
                double t = (terrAlts[i] - minAlt) / range; // 0=low, 1=high
                int rv, gv, bv;
                if      (t < 0.25) { double s = t / 0.25;        rv = 0;            gv = (int)(s * 255);       bv = 255; }
                else if (t < 0.5)  { double s = (t-0.25)/0.25;   rv = 0;            gv = 255;                  bv = (int)((1-s)*255); }
                else if (t < 0.75) { double s = (t-0.5)/0.25;    rv = (int)(s*255); gv = 255;                  bv = 0; }
                else               { double s = (t-0.75)/0.25;   rv = 255;          gv = (int)((1-s)*255);     bv = 0; }
                bmpData[idx]   = (byte)bv;
                bmpData[idx+1] = (byte)gv;
                bmpData[idx+2] = (byte)rv;
                bmpData[idx+3] = 140;
            }
            if (token.IsCancellationRequested) return;

            var newBmp = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppArgb);
            var bd = newBmp.LockBits(new Rectangle(0, 0, bmpW, bmpH),
                                     ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bmpData, 0, bd.Scan0, bmpData.Length);
            newBmp.UnlockBits(bd);

            Bitmap oldBmp;
            lock (_terLock) { oldBmp = _terBitmap; _terBitmap = newBmp; _terBounds = viewArea; }
            oldBmp?.Dispose();
            if (missingTerTiles) _terMissingTiles = true;
            try { gmap.Invoke((Action)(() => { if (_overlayMarker != null) _overlayMarker.Position = gmap.Position; gmap.Invalidate(); })); } catch { }
        }
        catch { }
    }

    private void ShowPlaceholder()
    {
        var gp = _zg.GraphPane;
        gp.CurveList.Clear();
        gp.GraphObjList.Clear();
        gp.Title.Text          = "Elevation Profile";
        gp.XAxis.Title.Text    = "Distance (m)";
        gp.YAxis.Title.Text    = "Altitude (m)";
        gp.Title.FontSpec.Size = 11;
        var msg = new TextObj("Set plane & target in CompanionPlugin", 0.5, 0.5, CoordType.PaneFraction);
        msg.FontSpec.Size             = 10;
        msg.FontSpec.IsBold           = false;
        msg.FontSpec.FontColor        = Color.Gray;
        msg.FontSpec.Border.IsVisible = false;
        msg.FontSpec.Fill.IsVisible   = false;
        gp.GraphObjList.Add(msg);
        gp.AxisChange();
        _zg.Invalidate();
    }

    private void ComputeAndPlotAsync(PointLatLngAlt start, PointLatLngAlt end, double planeAlt, double targetAlt, CancellationToken token)
    {
        try
        {
            if (start == null || end == null) return;
            double totalDist = start.GetDistance(end);
            if (totalDist < 1.0) return;

            int spacing = Math.Max(2, _sampleSpacingM);
            int samples = Math.Max(20, Math.Min(1500, (int)(totalDist / spacing)));

            var dists = new double[samples];
            var alts  = new double[samples];

            for (int i = 0; i < samples; i++)
            {
                if (token.IsCancellationRequested) return;
                double f = samples == 1 ? 0.0 : (double)i / (samples - 1);
                var p    = start.GetGreatCirclePathPoint(end, f);
                dists[i] = start.GetDistance(p);
                var r    = srtm.getAltitude(p.Lat, p.Lng);
                alts[i]  = r.currenttype == srtm.tiletype.valid ? r.alt : 0;
            }

            if (token.IsCancellationRequested) return;

            double planeAltASL   = alts[0]           + planeAlt;
            double targetAltASL  = alts[samples - 1]; // target always on the ground

            Host.MainForm.BeginInvoke((Action)(() => UpdatePlot(dists, alts, totalDist, planeAltASL, targetAltASL)));
        }
        catch { }
    }

    private void UpdatePlot(double[] dists, double[] alts, double totalDist, double planeAlt, double targetAlt)
    {
        try
        {
            if (_zg == null) return;
            var gp = _zg.GraphPane;
            gp.CurveList.Clear();
            gp.GraphObjList.Clear();

            gp.Title.IsVisible       = false;
            gp.Legend.IsVisible      = false;
            gp.XAxis.Title.IsVisible = false;
            gp.YAxis.Title.IsVisible = false;

            // ── compute min safe AGL (needed in both modes) ──────────────────
            double x0 = dists.Length > 0 ? dists[0]               : 0;
            double x1 = dists.Length > 0 ? dists[dists.Length - 1] : totalDist;
            double terrainAtPlane = alts.Length > 0 ? alts[0] : 0;
            double minPlaneASL = terrainAtPlane;
            if (x1 > x0)
            {
                for (int i = 1; i < dists.Length - 1; i++)
                {
                    double t = (dists[i] - x0) / (x1 - x0);
                    if (t >= 1.0) continue;
                    double needed = (alts[i] - t * targetAlt) / (1.0 - t);
                    if (needed > minPlaneASL) minPlaneASL = needed;
                }
            }
            double minPlaneAGL = Math.Max(0, minPlaneASL - terrainAtPlane);

            if (_minAltOnly)
            {
                // ── compact view: floating map label like Companion distance ─
                if (_panel != null) _panel.Visible = false;
                if (_minAltMapLabel != null)
                {
                    _minAltMapLabel.Text    = $"{minPlaneAGL:F1} m";
                    _minAltMapLabel.Visible = true;
                    _minAltMapLabel.BringToFront();
                }
                return;
            }

            // ── full graph mode: ensure panel visible, hide map label ────────
            if (_panel != null) _panel.Visible = true;
            if (_minAltMapLabel != null) _minAltMapLabel.Visible = false;

            // terrain fill curve
            var pts = new PointPairList();
            for (int i = 0; i < dists.Length; i++) pts.Add(dists[i], alts[i]);
            var curve = gp.AddCurve("", pts, Color.SaddleBrown, SymbolType.None);
            curve.Line.Width = 2.0f;
            curve.Line.Fill  = new Fill(Color.FromArgb(160, Color.SaddleBrown));

            // line-of-sight check
            bool hasObstacle = false;
            if (x1 > x0)
            {
                for (int i = 0; i < dists.Length; i++)
                {
                    double t      = (dists[i] - x0) / (x1 - x0);
                    double losAlt = planeAlt + t * (targetAlt - planeAlt);
                    if (alts[i] > losAlt) { hasObstacle = true; break; }
                }
            }

            Color losColor = hasObstacle ? Color.Red : Color.LimeGreen;
            var losPts = new PointPairList();
            losPts.Add(x0, planeAlt);
            losPts.Add(x1, targetAlt);
            var losCurve = gp.AddCurve("", losPts, losColor, SymbolType.Circle);
            losCurve.Line.Width  = 2.5f;
            losCurve.Symbol.Size = 6;
            losCurve.Symbol.Fill = new Fill(losColor);

            // min safe alt label (always green, top-left)
            var minAltLabel = new TextObj($"{minPlaneAGL:F1} m", 0.02, 0.04, CoordType.ChartFraction, AlignH.Left, AlignV.Top);
            minAltLabel.FontSpec.Size             = 16;
            minAltLabel.FontSpec.IsBold           = true;
            minAltLabel.FontSpec.FontColor        = Color.LimeGreen;
            minAltLabel.FontSpec.Fill.IsVisible   = false;
            minAltLabel.FontSpec.Border.IsVisible = false;
            gp.GraphObjList.Add(minAltLabel);

            // auto-scale, then pad Y
            gp.XAxis.Scale.MinAuto = true;
            gp.XAxis.Scale.MaxAuto = true;
            gp.YAxis.Scale.MinAuto = true;
            gp.YAxis.Scale.MaxAuto = true;
            gp.AxisChange();

            double ymin = Math.Min(gp.YAxis.Scale.Min, Math.Min(planeAlt, targetAlt));
            double ymax = Math.Max(gp.YAxis.Scale.Max, Math.Max(planeAlt, targetAlt));
            double pad  = Math.Max(5.0, (ymax - ymin) * 0.12);
            gp.YAxis.Scale.MinAuto = false;
            gp.YAxis.Scale.MaxAuto = false;
            gp.YAxis.Scale.Min = ymin - pad;
            gp.YAxis.Scale.Max = ymax + pad;

            // vertical marker lines
            gp.GraphObjList.Add(new LineObj(Color.Red,  x0, gp.YAxis.Scale.Min, x0, gp.YAxis.Scale.Max));
            gp.GraphObjList.Add(new LineObj(Color.Blue, x1, gp.YAxis.Scale.Min, x1, gp.YAxis.Scale.Max));

            gp.AxisChange();
            _zg.ZoomOutAll(_zg.GraphPane);
            _zg.Invalidate();
        }
        catch { }
    }

    // ── Exit ──────────────────────────────────────────────────────────────────
    public override bool Exit()
    {
        try { _cts?.Cancel();  } catch { }
        try { _vsCts?.Cancel(); } catch { }
        try { _terCts?.Cancel(); } catch { }

        lock (_terLock)
        {
            _terBitmap?.Dispose();
            _terBitmap = null;
        }

        try
        {
            if (_vsGmap != null)
            {
                _vsGmap.MouseMove   -= OnGmapMouseMove;
                _vsGmap.MouseLeave  -= OnGmapMouseLeave;
                try { _vsGmap.Overlays.Remove(_overlayLayer); } catch { }
                _overlayLayer?.Clear();
                _vsGmap = null;
            }
        }
        catch { }

        lock (_vsLock)
        {
            _viewshedBitmap?.Dispose();
            _viewshedBitmap = null;
        }

        try
        {
            if (_tabPage != null && Host?.MainForm?.FlightData != null)
            {
                var tc = Host.MainForm.FlightData.tabControlactions;
                if (tc.TabPages.Contains(_tabPage))
                    tc.TabPages.Remove(_tabPage);
                try { Host.MainForm.FlightData.TabListOriginal.Remove(_tabPage); } catch { }
                _tabPage.Dispose();
                _tabPage = null;
            }
        }
        catch { }

        try
        {
            if (_parent != null && _panel != null && _parent.Controls.Contains(_panel))
                _parent.Controls.Remove(_panel);
            var gmap = Host.FDGMapControl;
            if (gmap != null)
            {
                if (_minAltMapLabel != null && gmap.Controls.Contains(_minAltMapLabel))
                    gmap.Controls.Remove(_minAltMapLabel);
                if (_cursorAltLabel != null && gmap.Controls.Contains(_cursorAltLabel))
                    gmap.Controls.Remove(_cursorAltLabel);
            }
            _minAltMapLabel?.Dispose();
            _minAltMapLabel = null;
            _cursorAltLabel?.Dispose();
            _cursorAltLabel = null;
            Host.config["ElevationProfile.Width"] = _panel?.Width.ToString() ?? "350";
            try { Host.config.Save(); } catch { }
            _panel?.Dispose();
        }
        catch { }

        _zg = null;
        return true;
    }
}
