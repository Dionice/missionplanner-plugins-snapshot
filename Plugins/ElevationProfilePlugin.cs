using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;
using ZedGraph;

public class ElevationProfilePlugin : Plugin
{
    public override string Name    => "Elevation Profile";
    public override string Version => "0.3";
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

    // ────────────────────────────────────────────────────────────────────────
    public override bool Init()
    {
        loopratehz      = Host.config.GetInt32("ElevationProfile.LoopHz", 2);
        _sampleSpacingM = Host.config.GetInt32("ElevationProfile.SampleSpacingM", 10);
        _heightLocked   = (Host.config["ElevationProfile.HeightLocked"] ?? "True").ToString() != "False";
        _minAltOnly     = (Host.config["ElevationProfile.MinAltOnly"]   ?? "False").ToString() == "True";
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
                btnRefresh.Click += (s, e) => { _firstLoop = true; };
                _tabPage.Controls.Add(btnRefresh);

                var btnToggle = new Button() { Left = x1 + 118, Top = y, Width = 110, Height = 26, Text = "Show / Hide" };
                btnToggle.Click += (s, e) => ToggleVisible();
                _tabPage.Controls.Add(btnToggle);
                y += rowH + 10;

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

            if (double.IsNaN(pLat) || double.IsNaN(tLat)) return true;

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
        }
        catch { }
        return true;
    }

    // ── compute and plot ──────────────────────────────────────────────────────
    private void ScheduleUpdate(PointLatLngAlt start, PointLatLngAlt end, double planeAlt, double targetAlt)
    {
        lock (_updateLock)
        {
            try { _cts?.Cancel(); } catch { }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            if (start.Lat == 0 && start.Lng == 0 && end.Lat == 0 && end.Lng == 0)
            {
                Host.MainForm.BeginInvoke((Action)ShowPlaceholder);
                return;
            }

            Task.Run(() => ComputeAndPlotAsync(start, end, planeAlt, targetAlt, token), token);
        }
    }

    private void ShowPlaceholder()
    {
        if (_zg == null) return;
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
        try { _cts?.Cancel(); } catch { }

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
            if (gmap != null && _minAltMapLabel != null && gmap.Controls.Contains(_minAltMapLabel))
                gmap.Controls.Remove(_minAltMapLabel);
            _minAltMapLabel?.Dispose();
            _minAltMapLabel = null;
            Host.config["ElevationProfile.Width"] = _panel?.Width.ToString() ?? "350";
            try { Host.config.Save(); } catch { }
            _panel?.Dispose();
        }
        catch { }

        _zg = null;
        return true;
    }
}
