using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Windows.Forms;
using System.Globalization;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;
using MissionPlanner;
using MissionPlanner.GCSViews;
using MissionPlanner.Utilities;

namespace CompanionPlugin
{
    public class CompanionPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "CompanionPlugin"; } }
        public override string Version { get { return "0.2"; } }
        public override string Author { get { return "Dionice"; } }

        private double planeLat = double.NaN;
        private double planeLon = double.NaN;
        private double planeAlt = double.NaN;
        private double targetLat = double.NaN;
        private double targetLon = double.NaN;
        private double targetAlt = 0.0; // default 0 for manual target altitude

        private HttpListener listener;
        private Thread listenerThread;
        private int port = 8765;
        private bool running = false;
        private CancellationTokenSource listenerCts;
        private TextWriterTraceListener fileTraceListener = null;

        // per-socket send queues to ensure sequential SendAsync per socket
        private class SocketSendQueue
        {
            public WebSocket Ws;
            private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> queue = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
            private readonly SemaphoreSlim sem = new SemaphoreSlim(0);
            private readonly object runLock = new object();
            private Task loopTask;
            private bool stopping = false;

            public SocketSendQueue(WebSocket ws)
            {
                Ws = ws;
                loopTask = Task.Run(() => RunLoop());
            }

            public void Enqueue(byte[] data)
            {
                if (data == null) return;
                queue.Enqueue(data);
                try { sem.Release(); } catch { }
            }

            private async Task RunLoop()
            {
                try
                {
                    while (!stopping && Ws != null && Ws.State == WebSocketState.Open)
                    {
                        await sem.WaitAsync(TimeSpan.FromSeconds(30));
                        if (stopping) break;
                        while (queue.TryDequeue(out var item))
                        {
                            try
                            {
                                if (Ws.State != WebSocketState.Open) break;
                                await Ws.SendAsync(new ArraySegment<byte>(item), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                try { Trace.WriteLine("SocketSendQueue send failed: " + ex.Message + "\n" + ex.StackTrace); } catch { }
                                try { Ws.Abort(); } catch { }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { Trace.WriteLine("SocketSendQueue loop error: " + ex.Message + "\n" + ex.StackTrace); } catch { }
                }
            }

            public async Task StopAsync()
            {
                stopping = true;
                try { sem.Release(); } catch { }
                try { if (loopTask != null) await loopTask.ConfigureAwait(false); } catch (Exception ex) { try { Trace.WriteLine("SocketSendQueue stop error: " + ex.Message); } catch { } }
                try { if (Ws != null) Ws.Abort(); } catch (Exception ex) { try { Trace.WriteLine("SocketSendQueue abort failed: " + ex.Message); } catch { } }
                try { sem.Dispose(); } catch (Exception ex) { try { Trace.WriteLine("SocketSendQueue semaphore dispose failed: " + ex.Message); } catch { } }
            }
        }

        private readonly List<SocketSendQueue> sendQueues = new List<SocketSendQueue>();
        private readonly object socketsLock = new object();
        private readonly object stateLock = new object();

        // broadcast throttling
        private readonly object broadcastLock = new object();
        private long lastBroadcastMs = 0;
        private int broadcastIntervalMs = 200; // minimum ms between broadcasts (200ms = 5Hz)

        // map refresh debounce
        private int lastMapRefreshMs = 0;
        private int mapRefreshIntervalMs = 100; // ms between map Refresh calls (100ms = 10Hz)

        private GMapOverlay overlay;
        private GMapMarkerPlaneCustom planeMarker;
        private GMarkerGoogle targetMarker;
        private GMap.NET.WindowsForms.GMapMarker targetAltMarker;
        private GMap.NET.WindowsForms.GMapMarker planeAltMarker;
        // per-icon track routes and points
        private GMapRoute planeRoute;
        private GMapRoute targetRoute;
        private List<PointLatLng> planeTrackPoints = new List<PointLatLng>();
        private List<PointLatLng> targetTrackPoints = new List<PointLatLng>();
        // parallel altitude lists (meters) for KML export
        private List<double> planeTrackAlts = new List<double>();
        private List<double> targetTrackAlts = new List<double>();
        private Button btnSaveKml;
        private Button btnClearTracks;
        private CheckBox chkEnableTracks;
        private CheckBox chkShowPlaneTrack;
        private CheckBox chkShowTargetTrack;
        private Bitmap planeBmp;
        private Bitmap targetBmp;
        private int planeIconSize = 56;
        private TabPage tabPage;
        private Label tabLblPlane;
        private Label tabLblTarget;
        private Label tabLblAngle;
        private Label tabLblPlaneAlt;
        private Label tabLblTargetAlt;
        private Label tabLblPitch;
        private Label tabLblDistance;
        private NumericUpDown nudTargetAlt;
        private NumericUpDown nudTargetAltMin;
        private NumericUpDown nudTargetAltMax;
        private Label tabLblTargetAltMin;
        private Label tabLblTargetAltMax;
        private int lastTargetAltInt = int.MinValue;
        private int lastPlaneAltInt = int.MinValue;
        private Label tabLblPlaneAltDelta;
        private NumericUpDown nudPlaneAltDelta;
        private CheckBox chkEnableGimbal;
        private CheckBox chkUseRcControl;
        // drag/drop state
        private bool _isDraggingTarget = false;
        private bool _isDraggingPlane = false;
        private bool _prevCanDragMap = true;
        
        private bool _markerMouseOver = false;
        private bool _markerHandlersAttached = false;
        private IMessageFilter _globalMapFilter = null;
        private Label mapDistanceLabel = null;
        
        
        
        private Label tabLblGmId;
        
        
        
        private NumericUpDown nudGmId;
        
        
        private int planeAltTriggerDelta = 5; // default threshold (integer) to trigger SendGimbal
        private double targetAltMin = -100.0;
        private double targetAltMax = 1000.0;
        private Label tabLblDeviceEndpoint;
        private Label tabLblConeLen;
        private Label tabLblConeAngle;
        private NumericUpDown nudConeLen;
        private NumericUpDown nudConeAngle;
        private Label tabLblConeOffset;
        private NumericUpDown nudConeOffset;
        // Gimbal RC/PWM configuration
        private const int GIMBAL_PWM_MIN = 500;
        private const int GIMBAL_PWM_MAX = 2500;
        private const int GIMBAL_PWM_CENTER = 1500;
        private const double GIMBAL_PWM_PITCH_SCALE = 1000.0 / 90.0; // pitch degrees -> PWM delta
        private const double GIMBAL_PWM_YAW_SCALE = 1000.0 / 180.0;  // yaw degrees -> PWM delta
        private const int GIMBAL_RC_CHANNEL_PITCH_IDX = 15; // channel index (1-based) for pitch
        private const int GIMBAL_RC_CHANNEL_YAW_IDX = 16;   // channel index (1-based) for yaw

        // use Newtonsoft.Json (already referenced in project)

        public override bool Init()
        {
            this.loopratehz = 2;
            return true;
        }

        public override bool Loaded()
        {
            try
            {
                // create overlay
                try { overlay = new GMapOverlay("companion"); FlightData.instance.gMapControl1.Overlays.Add(overlay); } catch { }
                // ensure any pre-existing routes get the correct style
                try { if (overlay != null) { try { if (planeRoute != null) SetRouteStyle(planeRoute, true); } catch { } try { if (targetRoute != null) SetRouteStyle(targetRoute, false); } catch { } } } catch { }
                try { planeBmp = CreateCircleIcon(Color.Green, 20); } catch { planeBmp = null; }
                try
                {
                    // load embedded resource `planeicon` (planetracker.png) and scale to 20px
                    try
                    {
                        var resImg = global::MissionPlanner.Properties.Resources.planeicon;
                        if (resImg != null)
                        {
                            targetBmp = new Bitmap(resImg, new Size(56, 56));
                        }
                        else
                        {
                            targetBmp = CreateCircleIcon(Color.Blue, 20);
                        }
                    }
                    catch
                    {
                        targetBmp = CreateCircleIcon(Color.Blue, 20);
                    }
                }
                catch
                {
                    try { targetBmp = CreateCircleIcon(Color.Blue, 20); } catch { targetBmp = null; }
                }

                // load persisted positions from Settings if present
                try
                {
                    var s = MissionPlanner.Utilities.Settings.Instance;
                    if (s["Companion_plane_lat"] != null && s["Companion_plane_lon"] != null)
                    {
                        double lat, lon;
                        if (double.TryParse(s["Companion_plane_lat"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lat) &&
                            double.TryParse(s["Companion_plane_lon"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lon))
                        {
                            planeLat = lat; planeLon = lon;
                            UpdatePlaneMarker(new PointLatLng(planeLat, planeLon));
                        }
                    }
                    if (s["Companion_target_lat"] != null && s["Companion_target_lon"] != null)
                    {
                        double lat, lon;
                        if (double.TryParse(s["Companion_target_lat"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lat) &&
                            double.TryParse(s["Companion_target_lon"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lon))
                        {
                            targetLat = lat; targetLon = lon;
                            UpdateTargetMarker(new PointLatLng(targetLat, targetLon));
                        }
                    }
                    // load persisted target altitude (optional)
                    if (s["Companion_target_alt"] != null)
                    {
                        double altv;
                        if (double.TryParse(s["Companion_target_alt"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out altv))
                        {
                            targetAlt = altv;
                        }
                    }
                    // load persisted min/max target altitude (optional)
                    if (s["Companion_target_alt_min"] != null)
                    {
                        double v;
                        if (double.TryParse(s["Companion_target_alt_min"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                        {
                            targetAltMin = v;
                        }
                    }
                    if (s["Companion_target_alt_max"] != null)
                    {
                        double v;
                        if (double.TryParse(s["Companion_target_alt_max"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                        {
                            targetAltMax = v;
                        }
                    }
                    // load persisted plane altitude trigger delta (integer, default 5)
                    if (s["Companion_plane_alt_delta"] != null)
                    {
                        int dv;
                        if (int.TryParse(s["Companion_plane_alt_delta"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
                        {
                            planeAltTriggerDelta = Math.Max(1, dv);
                        }
                    }
                }
                catch { }

                StartServer();
                // add context menu items and a docked tab like the original GimbalTool
                try
                {
                    var setPlane = new ToolStripMenuItem("Set plane here");
                    setPlane.Click += (s, e) => {
                        try
                        {
                            var p = Host.FDMenuMapPosition;
                            if (p == null) return;
                            planeLat = p.Lat;
                            planeLon = p.Lng;
                            var pt = new PointLatLng(planeLat, planeLon);
                            try { FlightData.instance.gMapControl1.BeginInvoke(new Action(() => UpdatePlaneMarker(pt, true))); } catch { UpdatePlaneMarker(pt, true); }
                            try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPlane != null) tabLblPlane.Text = $"Plane: {planeLat:F6}, {planeLon:F6}"; })); } catch { }
                            double bearingP = ComputeBearing(planeLat, planeLon, targetLat, targetLon);
                            double pchP = ComputePitch(planeLat, planeLon, planeAlt, targetLat, targetLon, targetAlt);
                            if (!double.IsNaN(bearingP))
                            {
                                SendGimbal((float)pchP, (float)bearingP);
                                try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPitch != null) tabLblPitch.Text = $"Pitch: {pchP:F2}°"; })); } catch { }
                                try { double distKm = HaversineDistanceMeters(planeLat, planeLon, targetLat, targetLon) / 1000.0; FlightData.instance.BeginInvoke(new Action(() => { if (tabLblDistance != null) tabLblDistance.Text = $"Dist: {distKm:F3} km"; })); } catch { }
                            }
                        }
                        catch { }
                    };

                    var setTarget = new ToolStripMenuItem("Target is here");
                    setTarget.Click += (s, e) => {
                        try
                        {
                            var p = Host.FDMenuMapPosition;
                            if (p == null) return;
                            targetLat = p.Lat;
                            targetLon = p.Lng;
                            var pt = new PointLatLng(targetLat, targetLon);
                            try { FlightData.instance.gMapControl1.BeginInvoke(new Action(() => UpdateTargetMarker(pt, true))); } catch { UpdateTargetMarker(pt, true); }
                            try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblTarget != null) tabLblTarget.Text = $"Target: {targetLat:F6}, {targetLon:F6}"; })); } catch { }
                            double bearing = ComputeBearing(planeLat, planeLon, targetLat, targetLon);
                            double pch = ComputePitch(planeLat, planeLon, planeAlt, targetLat, targetLon, targetAlt);
                            try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblAngle != null) tabLblAngle.Text = double.IsNaN(bearing) ? "Bearing: N/A" : $"Bearing: {bearing:F2}°"; })); } catch { }
                            if (!double.IsNaN(bearing))
                            {
                                SendGimbal((float)pch, (float)bearing);
                                try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPitch != null) tabLblPitch.Text = $"Pitch: {pch:F2}°"; })); } catch { }
                                try { double distKm = HaversineDistanceMeters(planeLat, planeLon, targetLat, targetLon) / 1000.0; FlightData.instance.BeginInvoke(new Action(() => { if (tabLblDistance != null) tabLblDistance.Text = $"Dist: {distKm:F3} km"; })); } catch { }
                            }
                        }
                        catch { }
                    };

                    Host.FDMenuMap.Items.Insert(0, setPlane);
                    Host.FDMenuMap.Items.Insert(1, setTarget);

                    // create docked tab in FlightData
                    Action createTab = () =>
                    {
                        try
                        {
                            if (Host?.MainForm == null || Host.MainForm.FlightData == null) return;

                            tabPage = new TabPage();
                            tabPage.AutoScroll = true;
                            tabPage.Text = "GIMBAL COMPANION";
                            tabPage.Name = "tabGimbalCompanion";

                            tabLblPlane = new Label() { Left = 6, Top = 6, Width = 170, Text = "Plane: not set" };
                            tabLblTarget = new Label() { Left = 6, Top = 28, Width = 170, Text = "Target: not set" };
                            tabLblAngle = new Label() { Left = 6, Top = 50, Width = 170, Text = "Bearing: N/A" };
                            tabLblPlaneAlt = new Label() { Left = 180, Top = 6, Width = 140, Text = "Alt: N/A" };
                            // target alt label remains as a short label; the input box will sit to its right
                            tabLblTargetAlt = new Label() { Left = 180, Top = 28, Width = 26, Text = "Alt: " };
                            tabLblPitch = new Label() { Left = 180, Top = 52, Width = 140, Text = "Pitch: N/A" };
                            tabLblDistance = new Label() { Left = 180, Top = 76, Width = 140, Text = "Dist: N/A" };

                            

                            var btnSendTab = new Button() { Left = 6, Top = 112, Width = 120, Text = "Send Gimbal" };
                            // small visual polish for send button
                            try { btnSendTab.FlatStyle = FlatStyle.Flat; btnSendTab.Height = 28; btnSendTab.BackColor = Color.FromArgb(150, 200, 80); btnSendTab.ForeColor = Color.Black; } catch { }
                            // enable/disable gimbal control toggle (persisted) - default OFF
                            
                            // use a CheckBox with Appearance=Button to mimic a toggle switch
                            chkEnableGimbal = new CheckBox() { Left = 132, Top = 112, Width = 86, Height = 28, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Text = "OFF", Checked = false, TextAlign = ContentAlignment.MiddleCenter };
                            try {
                                var st = MissionPlanner.Utilities.Settings.Instance;
                                if (st["Companion_gimbal_enabled"] != null)
                                {
                                    bool v; if (bool.TryParse(st["Companion_gimbal_enabled"].ToString(), out v)) chkEnableGimbal.Checked = v;
                                }
                            } catch { }
                            // set initial Send button enabled state and toggle visuals to match checkbox
                            try { btnSendTab.Enabled = chkEnableGimbal.Checked; if (chkEnableGimbal.Checked) { chkEnableGimbal.Text = "ON"; chkEnableGimbal.BackColor = Color.Green; try { chkEnableGimbal.ForeColor = Color.Black; } catch { } } else { chkEnableGimbal.Text = "OFF"; try { chkEnableGimbal.BackColor = Color.Red; try { chkEnableGimbal.ForeColor = Color.White; } catch { } } catch { } } } catch { }
                            chkEnableGimbal.CheckedChanged += (sc, ec) => {
                                try {
                                    // update visuals
                                    if (chkEnableGimbal.Checked) { chkEnableGimbal.Text = "ON"; try { chkEnableGimbal.BackColor = Color.Green; try { chkEnableGimbal.ForeColor = Color.Black; } catch { } } catch { } }
                                    else { chkEnableGimbal.Text = "OFF"; try { chkEnableGimbal.BackColor = Color.Red; try { chkEnableGimbal.ForeColor = Color.White; } catch { } } catch { } }

                                    var st = MissionPlanner.Utilities.Settings.Instance;
                                    st["Companion_gimbal_enabled"] = chkEnableGimbal.Checked.ToString();
                                    try { st.Save(); } catch { }
                                    try { btnSendTab.Enabled = chkEnableGimbal.Checked; } catch { }

                                    // when toggled OFF, send RETRACT (DO_MOUNT_CONTROL mode 0)
                                    // when toggled ON, send RC_TARGETING (DO_MOUNT_CONTROL mode 3)
                                    try {
                                        if (!chkEnableGimbal.Checked)
                                        {
                                            try {
                                                if (MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen)
                                                {
                                                    MainV2.comPort.doCommand((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent,
                                                        MAVLink.MAV_CMD.DO_MOUNT_CONTROL,
                                                        0f, 0f, 0f, 0f, 0f, 0f, 0f, false);
                                                }
                                            } catch { }
                                        }
                                        else
                                        {
                                            try {
                                                if (MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen)
                                                {
                                                    MainV2.comPort.doCommand((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent,
                                                        MAVLink.MAV_CMD.DO_MOUNT_CONTROL,
                                                        0f, 0f, 0f, 0f, 0f, 0f, 3f, false);
                                                }
                                            } catch { }
                                        }
                                    } catch { }
                                } catch { }
                            };
                            // RC control checkbox: when checked, send RC_CHANNELS_OVERRIDE on channels 15(pitch) & 16(yaw)
                            // default to enabled (true) unless persisted setting is present
                            // place RC controls between mode selection and Send button
                            chkUseRcControl = new CheckBox() { Left = 6, Top = 84, Width = 120, Text = "Use RC control", Checked = true };
                            try { var st = MissionPlanner.Utilities.Settings.Instance; if (st["Companion_gimbal_rc"] != null) { bool v; if (bool.TryParse(st["Companion_gimbal_rc"].ToString(), out v)) chkUseRcControl.Checked = v; } } catch { }
                            chkUseRcControl.CheckedChanged += (sc2, ec2) => {
                                try {
                                    var st2 = MissionPlanner.Utilities.Settings.Instance;
                                    st2["Companion_gimbal_rc"] = chkUseRcControl.Checked.ToString();
                                    try { st2.Save(); } catch { }

                                    // change mount mode immediately: checked -> RC_TARGETING (3), unchecked -> MAVLINK_TARGETING (2)
                                    try {
                                        if (MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen)
                                        {
                                            float mode = chkUseRcControl.Checked ? 3f : 2f;
                                            MainV2.comPort.doCommand((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent,
                                                MAVLink.MAV_CMD.DO_MOUNT_CONTROL,
                                                0f, 0f, 0f, 0f, 0f, 0f, mode, false);
                                        }
                                    } catch { }
                                } catch { }
                            };
                            
                            int gmLabelLeft = 6;
                            int gmInputLeft = gmLabelLeft + 120;
                            // reduce spacing: place manager params closer to the RC control / Send button
                            int gmTopBase = chkUseRcControl.Top + 20;

                            // Use calculated pitch/yaw from plugin; no manual pitch/yaw inputs

                            // angle-only control: don't expose rate controls (rates are left unset / NaN)

                            

                            tabLblGmId = new Label() { Left = gmLabelLeft, Top = gmTopBase + 72, AutoSize = true, Text = "Gimbal ID:" };
                            nudGmId = new NumericUpDown() { Left = gmInputLeft, Top = gmTopBase + 72, Width = 80, Minimum = 0, Maximum = 255, DecimalPlaces = 0, Increment = 1 };
                            try { int v; if (int.TryParse(MissionPlanner.Utilities.Settings.Instance["Companion_gimbal_manager_id"]?.ToString() ?? string.Empty, out v)) nudGmId.Value = v; } catch { }
                            nudGmId.ValueChanged += (sa, ea) => { try { var st2 = MissionPlanner.Utilities.Settings.Instance; st2["Companion_gimbal_manager_id"] = nudGmId.Value.ToString(); try { st2.Save(); } catch { } } catch { } };

                            btnSendTab.Click += (s2, e2) => { double b2 = ComputeBearing(planeLat, planeLon, targetLat, targetLon); double pch = ComputePitch(planeLat, planeLon, planeAlt, targetLat, targetLon, targetAlt); if (!double.IsNaN(b2)) { SendGimbal((float)pch, (float)b2); tabLblAngle.Text = $"Bearing: {b2:F2}°"; try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPitch != null) tabLblPitch.Text = $"Pitch: {pch:F2}°"; })); } catch { } try { double distKm = HaversineDistanceMeters(planeLat, planeLon, targetLat, targetLon) / 1000.0; FlightData.instance.BeginInvoke(new Action(() => { if (tabLblDistance != null) tabLblDistance.Text = $"Dist: {distKm:F3} km"; })); } catch { } } };

                            // target altitude input (placed next to the Alt: label)
                            nudTargetAlt = new NumericUpDown() { Left = tabLblTargetAlt.Left + tabLblTargetAlt.Width, Top = 28, Width = 60, Minimum = (decimal)targetAltMin, Maximum = (decimal)targetAltMax, DecimalPlaces = 2, Increment = 0.5M };
                            try { nudTargetAlt.Value = (decimal)targetAlt; } catch { }
                            nudTargetAlt.ValueChanged += (s3, e3) => {
                                try {
                                    double newTargetAlt = (double)nudTargetAlt.Value;
                                    targetAlt = newTargetAlt;
                                    var st = MissionPlanner.Utilities.Settings.Instance;
                                    st["Companion_target_alt"] = targetAlt.ToString(CultureInfo.InvariantCulture);
                                    try { st.Save(); } catch { }

                                    // update UI input value (label remains static)
                                    try { /* tabLblTargetAlt stays as 'Alt:' label */ } catch { }

                                    // only trigger send when integer part changed
                                    try {
                                        int curInt = (int)newTargetAlt;
                                        if (curInt != lastTargetAltInt)
                                        {
                                            lastTargetAltInt = curInt;
                                            double b3 = ComputeBearing(planeLat, planeLon, targetLat, targetLon);
                                            double p3 = ComputePitch(planeLat, planeLon, planeAlt, targetLat, targetLon, targetAlt);
                                            if (!double.IsNaN(b3))
                                            {
                                                SendGimbal((float)p3, (float)b3);
                                                try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPitch != null) tabLblPitch.Text = $"Pitch: {p3:F2}°"; })); } catch { }
                                                try { double distKm = HaversineDistanceMeters(planeLat, planeLon, targetLat, targetLon) / 1000.0; FlightData.instance.BeginInvoke(new Action(() => { if (tabLblDistance != null) tabLblDistance.Text = $"Dist: {distKm:F3} km"; })); } catch { }
                                            }
                                        }
                                    } catch { }
                                    // broadcast updated positions (including alt) to web clients
                                    try { Task.Run(() => { try { BroadcastPositions(); } catch { } }); } catch { }
                                    // update/create altitude label marker immediately
                                    try {
                                        string lbl = $"{targetAlt:F1} m";
                                        if (overlay != null && targetMarker != null)
                                        {
                                            if (targetAltMarker == null)
                                            {
                                                try { var m = new GMapMarkerText(new PointLatLng(targetLat, targetLon), lbl) { PixelOffset = new Point(12, 12) }; targetAltMarker = m; overlay.Markers.Add(targetAltMarker); } catch { }
                                            }
                                            else
                                            {
                                                try { (targetAltMarker as GMapMarkerText).Text = lbl; } catch { }
                                                try { targetAltMarker.Position = new PointLatLng(targetLat, targetLon); } catch { }
                                            }
                                            try { RequestMapRefresh(); } catch { }
                                        }
                                    } catch { }
                                } catch { }
                            };

                            // plane-alt trigger delta control (integer threshold)
                            tabLblPlaneAltDelta = new Label() { Left = 6, Top = gmTopBase + 110, AutoSize = true, Text = $"Alt Delta to trigger: " };
                            nudPlaneAltDelta = new NumericUpDown() { Left = tabLblPlaneAltDelta.Left + tabLblPlaneAltDelta.Width+6, Top = gmTopBase + 110, AutoSize=true, Minimum = 1, Maximum = 1000, DecimalPlaces = 0, Increment = 1, Value = planeAltTriggerDelta };
                            nudPlaneAltDelta.ValueChanged += (sa, ea) => {
                                try {
                                    planeAltTriggerDelta = (int)nudPlaneAltDelta.Value;
                                    try { var st = MissionPlanner.Utilities.Settings.Instance; st["Companion_plane_alt_delta"] = planeAltTriggerDelta.ToString(CultureInfo.InvariantCulture); try { st.Save(); } catch { } } catch { }
                                    // try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPlaneAltDelta != null) tabLblPlaneAltDelta.Text = $"Alt Delta to trigger: {planeAltTriggerDelta}"; })); } catch { if (tabLblPlaneAltDelta != null) try { tabLblPlaneAltDelta.Text = $"Alt Delta to trigger: {planeAltTriggerDelta}"; } catch { } }
                                } catch { }
                            };

                            tabPage.Controls.Add(tabLblPlane);
                            tabPage.Controls.Add(tabLblTarget);
                            tabPage.Controls.Add(tabLblAngle);
                            tabPage.Controls.Add(tabLblPlaneAlt);
                            tabPage.Controls.Add(tabLblTargetAlt);
                            tabPage.Controls.Add(tabLblPitch);
                            tabPage.Controls.Add(tabLblDistance);
                            
                            tabPage.Controls.Add(tabLblPlaneAltDelta);
                            tabPage.Controls.Add(nudPlaneAltDelta);
                            tabPage.Controls.Add(chkEnableGimbal);
                            tabPage.Controls.Add(chkUseRcControl);
                            
                            
                            // no rate controls added (angle only)
                            tabPage.Controls.Add(tabLblGmId);
                            tabPage.Controls.Add(nudGmId);
                            try { chkEnableGimbal.BringToFront(); } catch { }
                            // target altitude min/max controls
                            tabLblTargetAltMin = new Label() { Left = 6, Top = gmTopBase + 142, AutoSize = true, Text = $"Target Alt Min: " };
                            nudTargetAltMin = new NumericUpDown() { Left = tabLblTargetAltMin.Left + tabLblTargetAltMin.Width + 6, Top = gmTopBase + 142, AutoSize = true, Minimum = -10000, Maximum = 100000, DecimalPlaces = 0, Increment = 1, Value = (decimal)targetAltMin };
                            tabLblTargetAltMax = new Label() { Left = 6, Top = gmTopBase + 170, AutoSize = true, Text = $"Target Alt Max: " };
                            nudTargetAltMax = new NumericUpDown() { Left = tabLblTargetAltMax.Left + tabLblTargetAltMax.Width + 6, Top = gmTopBase + 170, AutoSize = true, Minimum = -10000, Maximum = 100000, DecimalPlaces = 0, Increment = 1, Value = (decimal)targetAltMax };

                            nudTargetAltMin.ValueChanged += (sa, ea) => {
                                try {
                                    targetAltMin = (double)nudTargetAltMin.Value;
                                    try { var st = MissionPlanner.Utilities.Settings.Instance; st["Companion_target_alt_min"] = targetAltMin.ToString(CultureInfo.InvariantCulture); try { st.Save(); } catch { } } catch { }
                                    
                                    try { nudTargetAlt.Minimum = (decimal)targetAltMin; } catch { }
                                    try { Task.Run(() => { try { BroadcastPositions(); } catch { } }); } catch { }
                                } catch { }
                            };
                            nudTargetAltMax.ValueChanged += (sa, ea) => {
                                try {
                                    targetAltMax = (double)nudTargetAltMax.Value;
                                    try { var st = MissionPlanner.Utilities.Settings.Instance; st["Companion_target_alt_max"] = targetAltMax.ToString(CultureInfo.InvariantCulture); try { st.Save(); } catch { } } catch { }
                                    try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblTargetAltMax != null) tabLblTargetAltMax.Text = $"Target Alt Max: {targetAltMax:F0} m"; })); } catch { if (tabLblTargetAltMax != null) try { tabLblTargetAltMax.Text = $"Target Alt Max: {targetAltMax:F0} m"; } catch { } }
                                    try { nudTargetAlt.Maximum = (decimal)targetAltMax; } catch { }
                                        try { Task.Run(() => { try { BroadcastPositions(); } catch { } }); } catch { }
                                } catch { }
                            };

                            tabPage.Controls.Add(tabLblTargetAltMin);
                            tabPage.Controls.Add(nudTargetAltMin);
                            tabPage.Controls.Add(tabLblTargetAltMax);
                            tabPage.Controls.Add(nudTargetAltMax);
                            tabPage.Controls.Add(nudTargetAlt);
                            tabPage.Controls.Add(btnSendTab);
                            // Enable tracks checkbox
                            chkEnableTracks = new CheckBox() { Left = 6, Top = gmTopBase + 286, Width = 120, Text = "Enable Tracks", Checked = false };
                            try { var stt = MissionPlanner.Utilities.Settings.Instance; if (stt["Companion_tracks_enabled"] != null) { bool v; if (bool.TryParse(stt["Companion_tracks_enabled"].ToString(), out v)) chkEnableTracks.Checked = v; } } catch { }
                            chkEnableTracks.CheckedChanged += (sChk, eChk) => {
                                try {
                                    var stx = MissionPlanner.Utilities.Settings.Instance; stx["Companion_tracks_enabled"] = chkEnableTracks.Checked.ToString(); try { stx.Save(); } catch { }
                                    if (!chkEnableTracks.Checked)
                                    {
                                        // remove drawn routes
                                        try { if (overlay != null) { if (planeRoute != null) { overlay.Routes.Remove(planeRoute); planeRoute = null; } if (targetRoute != null) { overlay.Routes.Remove(targetRoute); targetRoute = null; } RequestMapRefresh(); } } catch { }
                                    }
                                    else
                                    {
                                        // recreate routes if we have points
                                        try { if (overlay != null) { if (planeRoute == null && planeTrackPoints != null && planeTrackPoints.Count > 0) { planeRoute = new GMapRoute(planeTrackPoints, "planeTrack"); try { SetRouteStyle(planeRoute, true); } catch { } overlay.Routes.Add(planeRoute); } if (targetRoute == null && targetTrackPoints != null && targetTrackPoints.Count > 0) { targetRoute = new GMapRoute(targetTrackPoints, "targetTrack"); try { SetRouteStyle(targetRoute, false); } catch { } overlay.Routes.Add(targetRoute); } RequestMapRefresh(); } } catch { }
                                    }
                                } catch { }
                            };
                            tabPage.Controls.Add(chkEnableTracks);
                            // per-track visibility toggles
                            chkShowPlaneTrack = new CheckBox() { Left = 6, Top = gmTopBase + 356, Width = 120, Text = "Show Plane Track", Checked = true };
                            try { var stp = MissionPlanner.Utilities.Settings.Instance; if (stp["Companion_plane_track_visible"] != null) { bool v; if (bool.TryParse(stp["Companion_plane_track_visible"].ToString(), out v)) chkShowPlaneTrack.Checked = v; } } catch { }
                            chkShowPlaneTrack.CheckedChanged += (spt, ept) => {
                                try { var stp2 = MissionPlanner.Utilities.Settings.Instance; stp2["Companion_plane_track_visible"] = chkShowPlaneTrack.Checked.ToString(); try { stp2.Save(); } catch { } } catch { }
                                try {
                                    if (overlay != null)
                                    {
                                        if (chkShowPlaneTrack.Checked)
                                        {
                                            if (planeRoute == null && planeTrackPoints != null && planeTrackPoints.Count > 0)
                                            {
                                                planeRoute = new GMapRoute(planeTrackPoints, "planeTrack");
                                                try { try { SetRouteStyle(planeRoute, true); } catch { } } catch { }
                                            }
                                        }
                                        else
                                        {
                                            try { if (planeRoute != null && overlay.Routes.Contains(planeRoute)) overlay.Routes.Remove(planeRoute); } catch { }
                                        }
                                        // enforce stable overlay order
                                        try { EnsureRoutesOverlayOrder(); } catch { }
                                    }
                                } catch { }
                            };
                            tabPage.Controls.Add(chkShowPlaneTrack);

                            chkShowTargetTrack = new CheckBox() { Left = chkShowPlaneTrack.Left + chkShowPlaneTrack.Width + 8, Top = gmTopBase + 356, Width = 120, Text = "Show Target Track", Checked = true };
                            try { var stt = MissionPlanner.Utilities.Settings.Instance; if (stt["Companion_target_track_visible"] != null) { bool v; if (bool.TryParse(stt["Companion_target_track_visible"].ToString(), out v)) chkShowTargetTrack.Checked = v; } } catch { }
                            chkShowTargetTrack.CheckedChanged += (stt2, ett2) => {
                                try { var stt3 = MissionPlanner.Utilities.Settings.Instance; stt3["Companion_target_track_visible"] = chkShowTargetTrack.Checked.ToString(); try { stt3.Save(); } catch { } } catch { }
                                try {
                                    if (overlay != null)
                                    {
                                        if (chkShowTargetTrack.Checked)
                                        {
                                            if (targetRoute == null && targetTrackPoints != null && targetTrackPoints.Count > 0)
                                            {
                                                targetRoute = new GMapRoute(targetTrackPoints, "targetTrack");
                                                try { try { SetRouteStyle(targetRoute, false); } catch { } } catch { }
                                            }
                                        }
                                        else
                                        {
                                            try { if (targetRoute != null && overlay.Routes.Contains(targetRoute)) overlay.Routes.Remove(targetRoute); } catch { }
                                        }
                                        // enforce stable overlay order
                                        try { EnsureRoutesOverlayOrder(); } catch { }
                                    }
                                } catch { }
                            };
                            tabPage.Controls.Add(chkShowTargetTrack);
                            // Save KML and Clear Tracks buttons
                            btnSaveKml = new Button() { Left = 6, Top = gmTopBase + 320, Width = 90, Height = 28, Text = "Save KML" };
                            try { btnSaveKml.FlatStyle = FlatStyle.Flat; btnSaveKml.BackColor = Color.FromArgb(220, 220, 220); } catch { }
                            btnSaveKml.Click += (s4, e4) => { try { SaveKml(); } catch { } };

                            btnClearTracks = new Button() { Left = btnSaveKml.Left + btnSaveKml.Width + 8, Top = btnSaveKml.Top, Width = 90, Height = 28, Text = "Clear Tracks" };
                            try { btnClearTracks.FlatStyle = FlatStyle.Flat; btnClearTracks.BackColor = Color.FromArgb(240, 200, 200); } catch { }
                            btnClearTracks.Click += (s5, e5) => { try { ClearTracks(); } catch { } };

                            tabPage.Controls.Add(btnSaveKml);
                            tabPage.Controls.Add(btnClearTracks);
                            // Gimbal cone visual configuration controls
                            tabLblConeLen = new Label() { Left = 6, Top = gmTopBase + 200, AutoSize = true, Text = "Cone length (km):" };
                            nudConeLen = new NumericUpDown() { Left = tabLblConeLen.Left + tabLblConeLen.Width + 6, Top = gmTopBase + 196, Width = 80, Minimum = 0.001M, Maximum = 10000M, DecimalPlaces = 3, Increment = 0.01M };
                            try { double v; if (double.TryParse(MissionPlanner.Utilities.Settings.Instance["Companion_gimbal_cone_len"]?.ToString() ?? string.Empty, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) nudConeLen.Value = (decimal)v; else nudConeLen.Value = 0.100M; } catch { try { nudConeLen.Value = 0.100M; } catch { } }
                            nudConeLen.ValueChanged += (sa, ea) => {
                                try {
                                    var st = MissionPlanner.Utilities.Settings.Instance;
                                    st["Companion_gimbal_cone_len"] = nudConeLen.Value.ToString(CultureInfo.InvariantCulture);
                                    try { st.Save(); } catch { }
                                    RequestMapRefresh();
                                } catch { }
                            };

                            tabLblConeAngle = new Label() { Left = 6, Top = gmTopBase + 228, AutoSize = true, Text = "Cone angle (deg):" };
                            nudConeAngle = new NumericUpDown() { Left = tabLblConeAngle.Left + tabLblConeAngle.Width + 6, Top = gmTopBase + 224, Width = 80, Minimum = 1, Maximum = 180, DecimalPlaces = 1, Increment = 0.5M };
                            try { float v2; if (float.TryParse(MissionPlanner.Utilities.Settings.Instance["Companion_gimbal_cone_angle"]?.ToString() ?? string.Empty, NumberStyles.Any, CultureInfo.InvariantCulture, out v2)) nudConeAngle.Value = (decimal)v2; else nudConeAngle.Value = 30; } catch { try { nudConeAngle.Value = 30; } catch { } }
                            nudConeAngle.ValueChanged += (sa, ea) => {
                                try {
                                    var st = MissionPlanner.Utilities.Settings.Instance;
                                    st["Companion_gimbal_cone_angle"] = nudConeAngle.Value.ToString(CultureInfo.InvariantCulture);
                                    try { st.Save(); } catch { }
                                    RequestMapRefresh();
                                } catch { }
                            };

                            tabPage.Controls.Add(tabLblConeLen);
                            tabPage.Controls.Add(nudConeLen);
                            tabPage.Controls.Add(tabLblConeAngle);
                            tabPage.Controls.Add(nudConeAngle);
                            // optional rotation offset to reconcile orientation conventions
                            tabLblConeOffset = new Label() { Left = 6, Top = gmTopBase + 256, AutoSize = true, Text = "Cone offset (deg):" };
                            nudConeOffset = new NumericUpDown() { Left = tabLblConeOffset.Left + tabLblConeOffset.Width + 6, Top = gmTopBase + 252, Width = 80, Minimum = -360, Maximum = 360, DecimalPlaces = 1, Increment = 1M };
                            try { double v; if (double.TryParse(MissionPlanner.Utilities.Settings.Instance["Companion_gimbal_cone_offset"]?.ToString() ?? string.Empty, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) nudConeOffset.Value = (decimal)v; else nudConeOffset.Value = 180M; } catch { try { nudConeOffset.Value = 180M; } catch { } }
                            nudConeOffset.ValueChanged += (sa, ea) => {
                                try {
                                    var st = MissionPlanner.Utilities.Settings.Instance;
                                    st["Companion_gimbal_cone_offset"] = nudConeOffset.Value.ToString(CultureInfo.InvariantCulture);
                                    try { st.Save(); } catch { }
                                    RequestMapRefresh();
                                } catch { }
                            };
                            tabPage.Controls.Add(tabLblConeOffset);
                            tabPage.Controls.Add(nudConeOffset);
                            // small spacer line after cone offset
                            var spacerAfterOffset = new Label() { Left = 6, Top = gmTopBase + 280, Width = 10, Height = 8, Text = "" };
                            tabPage.Controls.Add(spacerAfterOffset);
                            // device IP and port
                            string devip = "unknown";
                            try {
                                var host = Dns.GetHostEntry(Dns.GetHostName());
                                foreach (var ip in host.AddressList)
                                {
                                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip)) { devip = ip.ToString(); break; }
                                }
                            } catch { }
                            tabLblDeviceEndpoint = new Label() { Dock = DockStyle.Bottom, Text = $"Companion endpoint: {devip}:{port}" };
                            tabPage.Controls.Add(tabLblDeviceEndpoint);

                            Host.MainForm.FlightData.TabListOriginal.Add(tabPage);
                            var tabctrl = Host.MainForm.FlightData.tabControlactions;
                            if (!tabctrl.TabPages.Contains(tabPage)) tabctrl.TabPages.Insert(Math.Min(5, tabctrl.TabPages.Count), tabPage);
                            ThemeManager.ApplyThemeTo(tabPage);
                        }
                        catch (Exception ex2)
                        {
                            Trace.WriteLine("Companion createTab failed: " + ex2.Message);
                        }
                    };

                    if (FlightData.instance != null)
                    {
                        try { FlightData.instance.BeginInvoke(createTab); }
                        catch { createTab(); }
                    }
                    else
                    {
                        createTab();
                    }
                }
                catch { }

                // drag/drop handled by global message filter; no per-control handlers attached

                // attach marker enter/leave to disable map panning when cursor over marker
                try
                {
                    if (FlightData.instance != null && FlightData.instance.gMapControl1 != null && !_markerHandlersAttached)
                    {
                        FlightData.instance.gMapControl1.OnMarkerEnter += GMap_OnMarkerEnter;
                        FlightData.instance.gMapControl1.OnMarkerLeave += GMap_OnMarkerLeave;
                        _markerHandlersAttached = true;
                    }
                }
                catch { }

                // install a global message filter to intercept mouse messages before the map control
                try
                {
                    if (_globalMapFilter == null)
                    {
                        _globalMapFilter = new GlobalMapMouseFilter(this);
                        try { Application.AddMessageFilter(_globalMapFilter); } catch { }
                    }
                }
                catch { }

                // add a small top-center distance label on the map
                try
                {
                    var gmap = FlightData.instance?.gMapControl1;
                    if (gmap != null)
                    {
                        mapDistanceLabel = new Label()
                        {
                            AutoSize = false,
                            Width = 160,
                            Height = 24,
                            Text = "",
                            TextAlign = ContentAlignment.MiddleCenter,
                            BackColor = Color.FromArgb(200, 255, 255, 255),
                            ForeColor = Color.Black,
                            Font = new Font("Segoe UI", 12, FontStyle.Bold)
                        };
                        gmap.Controls.Add(mapDistanceLabel);
                        Action reposition = () => { try { mapDistanceLabel.Left = Math.Max(4, (gmap.Width - mapDistanceLabel.Width) / 2); mapDistanceLabel.Top = 25; } catch { } };
                        try { reposition(); } catch { }
                        try { gmap.Resize += (s, e) => { try { reposition(); } catch { } }; } catch { }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("CompanionPlugin load failed: " + ex.Message);
            }

            return true;
        }

        public override bool Loop()
        {
            try
            {
                // sync from Settings in case another plugin changed positions
                try
                {
                    var s = MissionPlanner.Utilities.Settings.Instance;
                    // sync target altitude if changed externally
                    try {
                        if (s["Companion_target_alt"] != null)
                        {
                            double altv;
                            if (double.TryParse(s["Companion_target_alt"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out altv))
                            {
                                if (Math.Abs(altv - targetAlt) > 0.0001)
                                {
                                    targetAlt = altv;
                                    try { if (FlightData.instance != null) FlightData.instance.BeginInvoke(new Action(() => { try { if (nudTargetAlt != null) { var v = (decimal)targetAlt; if (v < nudTargetAlt.Minimum) v = nudTargetAlt.Minimum; if (v > nudTargetAlt.Maximum) v = nudTargetAlt.Maximum; nudTargetAlt.Value = v; } } catch { } })); } catch { }
                                    try { BroadcastPositions(); } catch { }
                                    // update or create target altitude label marker immediately
                                    try {
                                        string lbl = $"{targetAlt:F1} m";
                                        if (overlay != null && targetMarker != null)
                                        {
                                            if (targetAltMarker == null)
                                            {
                                                try {
                                                    var m = new GMapMarkerText(new PointLatLng(targetLat, targetLon), lbl) { PixelOffset = new Point(12, 12) };
                                                    targetAltMarker = m;
                                                    overlay.Markers.Add(targetAltMarker);
                                                } catch { }
                                            }
                                            else
                                            {
                                                try { (targetAltMarker as GMapMarkerText).Text = lbl; } catch { }
                                                try { targetAltMarker.Position = new PointLatLng(targetLat, targetLon); } catch { }
                                            }
                                            try { RequestMapRefresh(); } catch { }
                                        }
                                    } catch { }
                                }
                            }
                        }
                    } catch { }
                    double lat, lon;
                    if (s["Companion_plane_lat"] != null && s["Companion_plane_lon"] != null
                        && double.TryParse(s["Companion_plane_lat"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lat)
                        && double.TryParse(s["Companion_plane_lon"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lon))
                    {
                        if (lat != planeLat || lon != planeLon)
                        {
                            planeLat = lat; planeLon = lon;
                            try { FlightData.instance.gMapControl1.BeginInvoke(new Action(() => UpdatePlaneMarker(new PointLatLng(planeLat, planeLon)))); } catch { UpdatePlaneMarker(new PointLatLng(planeLat, planeLon)); }
                        }
                    }
                    if (s["Companion_target_lat"] != null && s["Companion_target_lon"] != null
                        && double.TryParse(s["Companion_target_lat"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lat)
                        && double.TryParse(s["Companion_target_lon"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lon))
                    {
                        if (lat != targetLat || lon != targetLon)
                        {
                            targetLat = lat; targetLon = lon;
                            try { FlightData.instance.gMapControl1.BeginInvoke(new Action(() => UpdateTargetMarker(new PointLatLng(targetLat, targetLon)))); } catch { UpdateTargetMarker(new PointLatLng(targetLat, targetLon)); }
                        }
                    }
                }
                catch { }

                // refresh heading for plane marker if possible
                if (planeMarker != null)
                {
                    try
                    {
                        if (MainV2.comPort != null && MainV2.comPort.MAV != null && MainV2.comPort.MAV.cs != null)
                        {
                            float heading = MainV2.comPort.MAV.cs.yaw;
                            if (Math.Abs(heading - planeMarker.heading) > 0.5f)
                            {
                                planeMarker.heading = heading;
                                try { RequestMapRefresh(); } catch { }
                            }
                            // detect plane altitude integer changes (display units) and trigger gimbal
                            try
                            {
                                double planeAltDisplay = MainV2.comPort.MAV.cs.alt;
                                // update internal meters value for math
                                try { planeAlt = planeAltDisplay / CurrentState.multiplieralt; } catch { }
                                // update UI label
                                try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPlaneAlt != null) tabLblPlaneAlt.Text = double.IsNaN(planeAltDisplay) ? "Alt: N/A" : $"Alt: {planeAltDisplay:F2}"; })); } catch { if (tabLblPlaneAlt != null) try { tabLblPlaneAlt.Text = double.IsNaN(planeAltDisplay) ? "Alt: N/A" : $"Alt: {planeAltDisplay:F2}"; } catch { } }
                                // update or create plane altitude label marker immediately
                                try {
                                    string plbl = double.IsNaN(planeAltDisplay) ? "N/A" : $"{planeAltDisplay:F2} m";
                                    if (overlay != null && planeMarker != null)
                                    {
                                        if (planeAltMarker == null)
                                        {
                                            try { var m = new GMapMarkerText(new PointLatLng(planeMarker.Position.Lat, planeMarker.Position.Lng), plbl) { PixelOffset = new Point(12, 12) }; planeAltMarker = m; overlay.Markers.Add(planeAltMarker); } catch { }
                                        }
                                        else
                                        {
                                            try { (planeAltMarker as GMapMarkerText).Text = plbl; } catch { }
                                            try { planeAltMarker.Position = new PointLatLng(planeMarker.Position.Lat, planeMarker.Position.Lng); } catch { }
                                        }
                                        try { RequestMapRefresh(); } catch { }
                                    }
                                } catch { }

                                int curInt = (int)planeAltDisplay;
                                if (lastPlaneAltInt == int.MinValue)
                                {
                                    // first observation: initialize without triggering
                                    lastPlaneAltInt = curInt;
                                }
                                else if (Math.Abs(curInt - lastPlaneAltInt) >= planeAltTriggerDelta)
                                {
                                    try { Trace.WriteLine($"[Companion] plane alt int changed {lastPlaneAltInt} -> {curInt} (threshold {planeAltTriggerDelta})"); } catch { }
                                    lastPlaneAltInt = curInt;
                                    double bearing = ComputeBearing(planeLat, planeLon, targetLat, targetLon);
                                    double pitch = ComputePitch(planeLat, planeLon, planeAlt, targetLat, targetLon, targetAlt);
                                    if (!double.IsNaN(bearing))
                                    {
                                        try { Trace.WriteLine($"[Companion] sending gimbal due to plane alt change pitch={pitch:F2} yaw={bearing:F2}"); } catch { }
                                        SendGimbal((float)pitch, (float)bearing);
                                        try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPitch != null) tabLblPitch.Text = $"Pitch: {pitch:F2}°"; })); } catch { }
                                        try { double distKm = HaversineDistanceMeters(planeLat, planeLon, targetLat, targetLon) / 1000.0; FlightData.instance.BeginInvoke(new Action(() => { if (tabLblDistance != null) tabLblDistance.Text = $"Dist: {distKm:F3} km"; })); } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Loop error: " + ex.Message + "\n" + ex.StackTrace);
            }
            return true;
        }

        private void StartServer()
        {
            if (running) return;
            string prefixAny = $"http://+:{port}/";
            string prefixLocal = $"http://localhost:{port}/";
            listener = new HttpListener();
            try
            {
                // try to bind to all addresses first (may require URL ACL)
                listener.Prefixes.Add(prefixAny);
                listener.Start();
                // add file trace listener for persistent logs (only once)
                try
                {
                    if (fileTraceListener == null)
                    {
                        var dataDir = MissionPlanner.Utilities.Settings.GetDataDirectory();
                        var logPath = Path.Combine(dataDir ?? ".", "companion.log");
                        fileTraceListener = new TextWriterTraceListener(logPath);
                        Trace.Listeners.Add(fileTraceListener);
                        Trace.AutoFlush = true;
                        Trace.WriteLine("CompanionPlugin log started: " + DateTime.UtcNow.ToString("o"));
                    }
                }
                catch { }
                running = true;
                try { GMaps.Instance.Mode = AccessMode.ServerAndCache; } catch { }
                listenerCts = new CancellationTokenSource();
                listenerThread = new Thread(() => ListenerLoop(listenerCts.Token)) { IsBackground = true };
                listenerThread.Start();
                Trace.WriteLine($"CompanionPlugin listening on port {port} (all addresses)");
                    try {
                        string ip = "127.0.0.1";
                        try { var host = Dns.GetHostEntry(Dns.GetHostName()); foreach (var a in host.AddressList) { if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a)) { ip = a.ToString(); break; } } } catch { }
                        if (tabLblDeviceEndpoint != null)
                        {
                            try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblDeviceEndpoint != null) tabLblDeviceEndpoint.Text = $"Companion endpoint: {ip}:{port}"; })); }
                            catch { try { tabLblDeviceEndpoint.Text = $"Companion endpoint: {ip}:{port}"; } catch { } }
                        }
                    } catch { }
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("CompanionPlugin bind to + failed: " + ex.Message);
                try
                {
                    // fallback to localhost only (no URL ACL needed)
                    listener = new HttpListener();
                    listener.Prefixes.Add(prefixLocal);
                    listener.Start();
                    running = true;
                    listenerCts = new CancellationTokenSource();
                    listenerThread = new Thread(() => ListenerLoop(listenerCts.Token)) { IsBackground = true };
                    listenerThread.Start();
                    Trace.WriteLine($"CompanionPlugin listening on port {port} (localhost only)");
                        try {
                            string ip = "127.0.0.1";
                            if (tabLblDeviceEndpoint != null)
                            {
                                try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblDeviceEndpoint != null) tabLblDeviceEndpoint.Text = $"Companion endpoint: {ip}:{port}"; })); }
                                catch { try { tabLblDeviceEndpoint.Text = $"Companion endpoint: {ip}:{port}"; } catch { } }
                            }
                        } catch { }
                    return;
                }
                catch (Exception ex2)
                {
                    Trace.WriteLine("CompanionPlugin StartServer failed (localhost): " + ex2.Message);
                }
            }
        }

        private void ListenerLoop(CancellationToken token)
        {
            while (running && !token.IsCancellationRequested)
            {
                try
                {
                    var ctx = listener.GetContext();
                    if (token.IsCancellationRequested) break;
                    // run HandleContext and log any unhandled exceptions to Trace
                    Task.Run(async () => await HandleContext(ctx)).ContinueWith(t => {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            try { Trace.WriteLine("HandleContext task failed: " + t.Exception.Flatten().InnerException?.Message + "\n" + t.Exception.Flatten().InnerException?.StackTrace); } catch { }
                        }
                    });
                }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Trace.WriteLine("ListenerLoop error: " + ex.Message + "\n" + ex.StackTrace); }
            }
        }

        private void RequestMapRefresh()
        {
            try
            {
                int now = Environment.TickCount;
                if (now - lastMapRefreshMs < mapRefreshIntervalMs) return;
                lastMapRefreshMs = now;
                try
                {
                    if (FlightData.instance != null && FlightData.instance.gMapControl1 != null)
                    {
                        FlightData.instance.BeginInvoke(new Action(() => {
                            try { FlightData.instance.gMapControl1.Refresh(); } catch (Exception ex) { Trace.WriteLine("RequestMapRefresh UI error: " + ex.Message); }
                        }));
                    }
                }
                catch (Exception)
                {
                    try { if (FlightData.instance != null && FlightData.instance.gMapControl1 != null) FlightData.instance.gMapControl1.Refresh(); } catch (Exception ex) { Trace.WriteLine("RequestMapRefresh direct error: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                try { Trace.WriteLine("RequestMapRefresh error: " + ex.Message); } catch { }
            }
        }

        // per-control map handlers removed — global message filter handles drag/drop

            private void GMap_OnMarkerEnter(GMapMarker item)
            {
                try
                {
                    var control = FlightData.instance?.gMapControl1;
                    if (control == null) return;
                    // Only consider this enter if the mouse is within the same hit radius used for dragging
                    try
                    {
                        var pt = Control.MousePosition;
                        var local = control.PointToClient(pt);
                        var markPos = control.FromLatLngToLocal(item.Position);
                        var dx = markPos.X - local.X;
                        var dy = markPos.Y - local.Y;
                        int radius = 30;
                        if (dx * dx + dy * dy > radius * radius) return; // ignore enter outside drag radius
                    }
                    catch { /* fall through to safe behavior */ }
                    if (_markerMouseOver) return;
                    _markerMouseOver = true;
                    try { _prevCanDragMap = control.CanDragMap; control.CanDragMap = false; } catch { }
                    try { control.Cursor = Cursors.Hand; } catch { }
                }
                catch { }
            }

            private void GMap_OnMarkerLeave(GMapMarker item)
            {
                try
                {
                    var control = FlightData.instance?.gMapControl1;
                    if (control == null) return;
                    // If mouse still within drag radius, don't treat this as a leave
                    try
                    {
                        var pt = Control.MousePosition;
                        var local = control.PointToClient(pt);
                        var markPos = control.FromLatLngToLocal(item.Position);
                        var dx = markPos.X - local.X;
                        var dy = markPos.Y - local.Y;
                        int radius = 30;
                        if (dx * dx + dy * dy <= radius * radius) return; // still inside
                    }
                    catch { /* fall through to default leave behavior */ }
                    if (!_markerMouseOver) return;
                    _markerMouseOver = false;
                    try { control.CanDragMap = _prevCanDragMap; } catch { }
                    try { control.Cursor = Cursors.Default; } catch { }
                }
                catch { }
            }

        private async Task HandleContext(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var resp = ctx.Response;

                if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/ws")
                {
                    // WebSocket upgrade
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        try
                        {
                            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                            var ws = wsCtx.WebSocket;
                            SocketSendQueue sq = null;
                            try { lock (socketsLock) { sq = new SocketSendQueue(ws); sendQueues.Add(sq); } } catch (Exception ex) { Trace.WriteLine("Add sendqueue failed: " + ex.Message); }
                            try
                            {
                                // enqueue initial positions snapshot
                                double s_planeLat, s_planeLon, s_planeAlt, s_targetLat, s_targetLon, s_targetAlt, s_targetMin, s_targetMax;
                                lock (stateLock)
                                {
                                    s_planeLat = planeLat; s_planeLon = planeLon; s_planeAlt = planeAlt;
                                    s_targetLat = targetLat; s_targetLon = targetLon; s_targetAlt = targetAlt; s_targetMin = targetAltMin; s_targetMax = targetAltMax;
                                }
                                var obj = new { type = "positions", payload = new { plane = new { lat = s_planeLat, lon = s_planeLon, alt = s_planeAlt }, target = new { lat = s_targetLat, lon = s_targetLon, alt = s_targetAlt, min = s_targetMin, max = s_targetMax } } };
                                var s = JsonConvert.SerializeObject(obj);
                                var b = Encoding.UTF8.GetBytes(s);
                                sq?.Enqueue(b);
                            }
                            catch (Exception ex) { Trace.WriteLine("Initial send enqueue failed: " + ex.Message); }
                            await ReceiveLoop(ws);
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine("WS accept failed: " + ex.Message);
                        }
                        return;
                    }
                }

                if (req.HttpMethod == "GET")
                {
                    if (req.Url.AbsolutePath == "/" || req.Url.AbsolutePath == "/index.html")
                    {
                        var html = GetWebUIHtml(port);
                        var b = Encoding.UTF8.GetBytes(html);
                        resp.ContentType = "text/html; charset=utf-8";
                        resp.ContentLength64 = b.Length;
                        await resp.OutputStream.WriteAsync(b, 0, b.Length);
                        resp.OutputStream.Close();
                        return;
                    }

                    // serve local static files (search multiple possible locations)
                    if (req.HttpMethod == "GET" && req.Url.AbsolutePath.StartsWith("/static/"))
                    {
                        try
                        {
                            var rel = req.Url.AbsolutePath.Substring("/static/".Length).TrimStart('/');
                            var relPath = rel.Replace('/', Path.DirectorySeparatorChar);

                            var candidates = new List<string>();
                            try { candidates.Add(Path.Combine(MissionPlanner.Utilities.Settings.GetDataDirectory(), "companion_web")); } catch { }
                            try {
                                var appBase = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
                                if (!string.IsNullOrEmpty(appBase))
                                {
                                    candidates.Add(Path.Combine(appBase, "companion_web"));
                                    candidates.Add(Path.Combine(appBase, "plugins", "companion_web"));
                                    candidates.Add(Path.Combine(appBase, "plugins"));
                                }
                            } catch { }

                            string foundPath = null;
                            foreach (var baseDir in candidates)
                            {
                                if (string.IsNullOrEmpty(baseDir)) continue;
                                try
                                {
                                    var filePath = Path.Combine(baseDir, relPath);
                                    if (File.Exists(filePath)) { foundPath = filePath; break; }
                                }
                                catch { }
                            }

                            if (foundPath != null)
                            {
                                var data = await ReadFileAsync(foundPath);
                                if (data == null) { resp.StatusCode = 500; resp.ContentLength64 = 0; try { resp.OutputStream.Close(); } catch { } return; }
                                var ext = Path.GetExtension(foundPath).ToLower();
                                var contentType = "application/octet-stream";
                                if (ext == ".js") contentType = "application/javascript";
                                else if (ext == ".css") contentType = "text/css";
                                else if (ext == ".png") contentType = "image/png";
                                else if (ext == ".jpg" || ext == ".jpeg") contentType = "image/jpeg";
                                else if (ext == ".svg") contentType = "image/svg+xml";

                                resp.ContentType = contentType;
                                resp.ContentLength64 = data.Length;
                                await resp.OutputStream.WriteAsync(data, 0, data.Length);
                                resp.OutputStream.Close();
                                return;
                            }

                            resp.StatusCode = 404; resp.ContentLength64 = 0; try { resp.OutputStream.Close(); } catch { }
                            return;
                        }
                        catch { resp.StatusCode = 500; resp.ContentLength64 = 0; try { resp.OutputStream.Close(); } catch { } return; }
                    }

                    if (req.Url.AbsolutePath == "/providers")
                    {
                        try
                        {
                            var list = new List<string>() {
                                "OpenStreetMap",
                                "EsriWorldImagery",
                                "CartoDB_Positron",
                                "Stamen_Toner",
                                "Stamen_Terrain",
                                "OpenTopoMap"
                            };
                            var s = JsonConvert.SerializeObject(list);
                            var b = Encoding.UTF8.GetBytes(s);
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = b.Length;
                            await resp.OutputStream.WriteAsync(b, 0, b.Length);
                            resp.OutputStream.Close();
                            return;
                        }
                        catch { resp.StatusCode = 500; resp.OutputStream.Close(); return; }
                    }

                    // server-side tile proxy/cache is disabled: let clients fetch tiles directly
                    if (req.Url.AbsolutePath.StartsWith("/gmapcache/TileDBv3/"))
                    {
                        resp.StatusCode = 404; resp.ContentLength64 = 0; try { resp.OutputStream.Close(); } catch { } return;
                    }

                    // server-side tile proxy is disabled; return 404 for /tiles requests
                    if (req.Url.AbsolutePath.StartsWith("/tiles/"))
                    {
                        resp.StatusCode = 404; resp.ContentLength64 = 0; try { resp.OutputStream.Close(); } catch { } return;
                    }

                                if (req.Url.AbsolutePath == "/positions")
                                    {
                                        var obj = new { plane = new { lat = planeLat, lon = planeLon, alt = planeAlt }, target = new { lat = targetLat, lon = targetLon, alt = targetAlt, min = targetAltMin, max = targetAltMax } };
                                        var s = JsonConvert.SerializeObject(obj);
                                        var b = Encoding.UTF8.GetBytes(s);
                                        resp.ContentType = "application/json";
                                        resp.ContentLength64 = b.Length;
                                        await resp.OutputStream.WriteAsync(b, 0, b.Length);
                                        resp.OutputStream.Close();
                                        return;
                                    }
                }

                if (req.HttpMethod == "POST")
                {
                    using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                    {
                        var body = sr.ReadToEnd();
                        if (req.Url.AbsolutePath == "/plane")
                        {
                            try
                            {
                                var d = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                                if (d.ContainsKey("lat") && d.ContainsKey("lon"))
                                {
                                    var lat = Convert.ToDouble(d["lat"]);
                                    var lon = Convert.ToDouble(d["lon"]);
                                    lock (stateLock) { planeLat = lat; planeLon = lon; }
                                    UpdatePlaneMarker(new PointLatLng(lat, lon));
                                }
                            }
                            catch (Exception ex) { Trace.WriteLine("/plane parse failed: " + ex.Message + "\n" + ex.StackTrace); }
                        }
                        else if (req.Url.AbsolutePath == "/target")
                        {
                            try
                            {
                                var d = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                                if (d.ContainsKey("lat") && d.ContainsKey("lon"))
                                {
                                    var lat = Convert.ToDouble(d["lat"]);
                                    var lon = Convert.ToDouble(d["lon"]);
                                    lock (stateLock) { targetLat = lat; targetLon = lon; }
                                    // accept optional altitude in meters
                                    if (d.ContainsKey("alt"))
                                    {
                                        try { targetAlt = Convert.ToDouble(d["alt"]); }
                                        catch { }
                                        try { var st = MissionPlanner.Utilities.Settings.Instance; st["Companion_target_alt"] = targetAlt.ToString(CultureInfo.InvariantCulture); try { st.Save(); } catch { } } catch { }
                                    }
                                    UpdateTargetMarker(new PointLatLng(lat, lon));
                                }
                            }
                            catch (Exception ex) { Trace.WriteLine("/target parse failed: " + ex.Message + "\n" + ex.StackTrace); }
                        }
                    }

                    // simple OK
                    resp.StatusCode = 200;
                    resp.ContentLength64 = 0;
                    try { resp.OutputStream.Close(); } catch { }
                    return;
                }

                // default 404
                resp.StatusCode = 404;
                resp.ContentLength64 = 0;
                try { resp.OutputStream.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("HandleContext error: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private async Task ReceiveLoop(WebSocket ws)
        {
            var buf = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult result = null;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
                                break;
                            }
                            ms.Write(buf, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result == null || result.MessageType == WebSocketMessageType.Close) break;

                        ms.Position = 0;
                        string msg = null;
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            using (var sr = new StreamReader(ms, Encoding.UTF8))
                                msg = await sr.ReadToEndAsync();
                        }
                        else
                        {
                            // ignore binary messages
                            continue;
                        }

                        // expect JSON { action: "set", target: "plane"|"target", lat: x, lon: y }
                        try
                        {
                            var d = JsonConvert.DeserializeObject<Dictionary<string, object>>(msg);
                            if (d != null && d.ContainsKey("action") && d["action"].ToString() == "set" && d.ContainsKey("target") && d.ContainsKey("lat") && d.ContainsKey("lon"))
                            {
                                var target = d["target"].ToString();
                                var lat = Convert.ToDouble(d["lat"]);
                                var lon = Convert.ToDouble(d["lon"]);
                                if (target == "plane")
                                {
                                    lock (stateLock) { planeLat = lat; planeLon = lon; }
                                    UpdatePlaneMarker(new PointLatLng(lat, lon));
                                }
                                else
                                {
                                    lock (stateLock) { targetLat = lat; targetLon = lon; }
                                    UpdateTargetMarker(new PointLatLng(lat, lon));
                                }
                            }
                        }
                        catch (Exception ex) { Trace.WriteLine("WS msg parse failed: " + ex.Message + "\n" + ex.StackTrace); }
                    }
                }
            }
            catch (Exception ex) { Trace.WriteLine("WS receive loop error: " + ex.Message + "\n" + ex.StackTrace); }
            finally
            {
                try { lock (socketsLock) { var found = sendQueues.Find(x => x.Ws == ws); if (found != null) { sendQueues.Remove(found); Task.Run(() => found.StopAsync()); } } } catch (Exception ex) { Trace.WriteLine("Remove sendqueue failed: " + ex.Message); }
                try { ws.Abort(); } catch (Exception ex) { Trace.WriteLine("WS abort failed: " + ex.Message); }
            }
        }

        private async Task SendPositionsToSocket(WebSocket ws)
        {
            // snapshot state under lock to avoid races while serializing
            double s_planeLat, s_planeLon, s_planeAlt, s_targetLat, s_targetLon, s_targetAlt, s_targetMin, s_targetMax;
            lock (stateLock)
            {
                s_planeLat = planeLat; s_planeLon = planeLon; s_planeAlt = planeAlt;
                s_targetLat = targetLat; s_targetLon = targetLon; s_targetAlt = targetAlt; s_targetMin = targetAltMin; s_targetMax = targetAltMax;
            }
            var obj = new { type = "positions", payload = new { plane = new { lat = s_planeLat, lon = s_planeLon, alt = s_planeAlt }, target = new { lat = s_targetLat, lon = s_targetLon, alt = s_targetAlt, min = s_targetMin, max = s_targetMax } } };
            var s = JsonConvert.SerializeObject(obj);
            var b = Encoding.UTF8.GetBytes(s);
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    // if we have a send-queue for this ws, enqueue instead
                    SocketSendQueue found = null;
                    try { lock (socketsLock) { found = sendQueues.Find(x => x.Ws == ws); } } catch { }
                    if (found != null)
                    {
                        found.Enqueue(b);
                    }
                    else
                    {
                        await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SendPositionsToSocket failed: " + ex.Message);
                // remove dead socket
                try { lock (socketsLock) { var f = sendQueues.Find(x => x.Ws == ws); if (f != null) { sendQueues.Remove(f); Task.Run(() => f.StopAsync()); } } } catch (Exception sex) { Trace.WriteLine("SendPositions remove failed: " + sex.Message); }
                try { ws.Abort(); } catch (Exception aex) { Trace.WriteLine("SendPositions Abort failed: " + aex.Message); }
            }
        }

        private async Task<byte[]> ReadFileAsync(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                {
                    long lenL = fs.Length;
                    if (lenL > int.MaxValue) throw new IOException("File too large");
                    int len = (int)lenL;
                    var buf = new byte[len];
                    int read = 0;
                    while (read < len)
                    {
                        int r = await fs.ReadAsync(buf, read, len - read);
                        if (r == 0) break;
                        read += r;
                    }
                    if (read != len)
                    {
                        var outb = new byte[read];
                        Array.Copy(buf, outb, read);
                        return outb;
                    }
                    return buf;
                }
            }
            catch (Exception ex)
            {
                try { Trace.WriteLine("ReadFileAsync failed: " + ex.Message + "\n" + ex.StackTrace); } catch { }
                return null;
            }
        }

        private void BroadcastPositions()
        {
            // rate-limit broadcasts to avoid Task explosion when updates are frequent
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (broadcastLock)
            {
                if (nowMs - lastBroadcastMs < broadcastIntervalMs) return;
                lastBroadcastMs = nowMs;
            }

            // snapshot state
            double s_planeLat, s_planeLon, s_planeAlt, s_targetLat, s_targetLon, s_targetAlt, s_targetMin, s_targetMax;
            lock (stateLock)
            {
                s_planeLat = planeLat; s_planeLon = planeLon; s_planeAlt = planeAlt;
                s_targetLat = targetLat; s_targetLon = targetLon; s_targetAlt = targetAlt; s_targetMin = targetAltMin; s_targetMax = targetAltMax;
            }
            var obj = new { type = "positions", payload = new { plane = new { lat = s_planeLat, lon = s_planeLon, alt = s_planeAlt }, target = new { lat = s_targetLat, lon = s_targetLon, alt = s_targetAlt, min = s_targetMin, max = s_targetMax } } };
            var s = JsonConvert.SerializeObject(obj);
            var b = Encoding.UTF8.GetBytes(s);

            // enqueue to per-socket send queues (sequential SendAsync per socket)
            SocketSendQueue[] arr;
            lock (socketsLock) { arr = sendQueues.ToArray(); }
            foreach (var sq in arr)
            {
                try
                {
                    if (sq == null || sq.Ws == null || sq.Ws.State != WebSocketState.Open)
                    {
                        try { lock (socketsLock) { if (sendQueues.Contains(sq)) sendQueues.Remove(sq); } } catch (Exception ex) { Trace.WriteLine("Broadcast remove failed: " + ex.Message); }
                        continue;
                    }
                    sq.Enqueue(b);
                }
                catch (Exception ex)
                {
                    try { Trace.WriteLine("Broadcast enqueue failed: " + ex.Message + "\n" + ex.StackTrace); } catch { }
                }
            }
        }

        private void UpdatePlaneMarker(PointLatLng p, bool persist = true)
        {
            try
            {
                // ensure globals reflect this update so broadcasts are accurate
                try { lock (stateLock) { planeLat = p.Lat; planeLon = p.Lng; } } catch { }

                if (overlay == null) return;
                try
                {
                    float heading = 0f;
                    try { if (MainV2.comPort != null && MainV2.comPort.MAV != null && MainV2.comPort.MAV.cs != null) heading = MainV2.comPort.MAV.cs.yaw; } catch { }

                    if (planeMarker != null)
                    {
                        planeMarker.Position = p;
                        planeMarker.heading = heading;
                    }
                    else
                    {
                        planeMarker = new GMapMarkerPlaneCustom(p, heading, planeIconSize);
                        overlay.Markers.Add(planeMarker);
                    }
                    // update/create plane altitude label marker centered on plane
                    try
                    {
                        string lbl = double.IsNaN(planeAlt) ? "N/A" : $"{planeAlt:F1} m";
                        if (planeAltMarker == null)
                        {
                            try { var m = new GMapMarkerText(new PointLatLng(p.Lat, p.Lng), lbl) { PixelOffset = new Point(12, 12) }; planeAltMarker = m; overlay.Markers.Add(planeAltMarker); } catch { }
                        }
                        else
                        {
                            try { (planeAltMarker as GMapMarkerText).Text = lbl; } catch { }
                            try { planeAltMarker.Position = new PointLatLng(p.Lat, p.Lng); } catch { }
                        }
                    }
                    catch { }
                }
                catch { }
                try { RequestMapRefresh(); } catch { }

                // append to plane track when position changed significantly (only when tracks enabled)
                try
                {
                    if (TracksEnabled())
                    {
                        if (planeTrackPoints == null) planeTrackPoints = new List<PointLatLng>();
                        bool add = false;
                        if (planeTrackPoints.Count == 0) add = true;
                        else
                        {
                            var last = planeTrackPoints[planeTrackPoints.Count - 1];
                            double d = HaversineDistanceMeters(last.Lat, last.Lng, p.Lat, p.Lng);
                            if (d >= 1.0) add = true; // only record if moved >= 1 meter
                        }
                        if (add)
                        {
                            planeTrackPoints.Add(p);
                            try { if (planeTrackAlts == null) planeTrackAlts = new List<double>(); planeTrackAlts.Add(double.IsNaN(planeAlt) ? 0.0 : planeAlt); } catch { }
                            try { Trace.WriteLine($"PlaneTrack add: {p.Lat.ToString(CultureInfo.InvariantCulture)},{p.Lng.ToString(CultureInfo.InvariantCulture)}"); } catch { }

                            if (planeRoute == null) planeRoute = new GMapRoute(planeTrackPoints, "planeTrack");
                            planeRoute.Points.Clear();
                            planeRoute.Points.AddRange(planeTrackPoints);
                            try { try { SetRouteStyle(planeRoute, true); } catch { } } catch { }

                            if (overlay != null)
                            {
                                if (TracksPlaneVisible())
                                {
                                    try { if (overlay.Routes.Contains(planeRoute)) overlay.Routes.Remove(planeRoute); } catch { }
                                    try { try { SetRouteStyle(planeRoute, true); } catch { } overlay.Routes.Add(planeRoute); } catch { }
                                }
                                else
                                {
                                    try { if (overlay.Routes.Contains(planeRoute)) overlay.Routes.Remove(planeRoute); } catch { }
                                }
                                try { RequestMapRefresh(); } catch { }
                            }
                        }
                    }
                }
                catch { }

                if (persist)
                {
                    try
                    {
                        var s = MissionPlanner.Utilities.Settings.Instance;
                        s["Companion_plane_lat"] = p.Lat.ToString(CultureInfo.InvariantCulture);
                        s["Companion_plane_lon"] = p.Lng.ToString(CultureInfo.InvariantCulture);
                        try { s.Save(); } catch { }
                    }
                    catch { }
                }

                // update tab UI labels and send gimbal mount command
                try
                {
                    double bearing = ComputeBearing(p.Lat, p.Lng, targetLat, targetLon);
                    // read plane altitude: keep `planeAlt` in meters for math, but display in current units
                    double planeAltDisplay = double.NaN;
                    try { if (MainV2.comPort != null && MainV2.comPort.MAV != null && MainV2.comPort.MAV.cs != null) { planeAltDisplay = MainV2.comPort.MAV.cs.alt; planeAlt = planeAltDisplay / CurrentState.multiplieralt; } } catch { }
                    try { FlightData.instance.BeginInvoke(new Action(() => {
                        try { if (tabLblPlane != null) tabLblPlane.Text = $"Plane: {p.Lat:F6}, {p.Lng:F6}"; } catch { }
                        try { if (tabLblAngle != null) tabLblAngle.Text = double.IsNaN(bearing) ? "Bearing: N/A" : $"Bearing: {bearing:F2}°"; } catch { }
                        try { if (tabLblPlaneAlt != null) tabLblPlaneAlt.Text = double.IsNaN(planeAltDisplay) ? "Alt: N/A" : $"Alt: {planeAltDisplay:F2}"; } catch { }
                        try {
                            string plbl = double.IsNaN(planeAltDisplay) ? "N/A" : $"{planeAltDisplay:F2} m";
                            if (overlay != null && planeMarker != null)
                            {
                                if (planeAltMarker == null)
                                {
                                    try { var m = new GMapMarkerText(new PointLatLng(p.Lat, p.Lng), plbl) { PixelOffset = new Point(12, 12) }; planeAltMarker = m; overlay.Markers.Add(planeAltMarker); } catch { }
                                }
                                else
                                {
                                    try { (planeAltMarker as GMapMarkerText).Text = plbl; } catch { }
                                    try { planeAltMarker.Position = new PointLatLng(p.Lat, p.Lng); } catch { }
                                }
                                try { RequestMapRefresh(); } catch { }
                            }
                        } catch { }
                    })); } catch {
                        try { if (tabLblPlane != null) tabLblPlane.Text = $"Plane: {p.Lat:F6}, {p.Lng:F6}"; } catch { }
                        try { if (tabLblAngle != null) tabLblAngle.Text = double.IsNaN(bearing) ? "Bearing: N/A" : $"Bearing: {bearing:F2}°"; } catch { }
                        try { if (tabLblPlaneAlt != null) tabLblPlaneAlt.Text = double.IsNaN(planeAltDisplay) ? "Alt: N/A" : $"Alt: {planeAltDisplay:F2}"; } catch { }
                    }
                    if (!double.IsNaN(bearing))
                    {
                        double pitch = ComputePitch(p.Lat, p.Lng, planeAlt, targetLat, targetLon, targetAlt);
                        try { SendGimbal((float)pitch, (float)bearing); } catch { }
                        try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPitch != null) tabLblPitch.Text = $"Pitch: {pitch:F2}°"; })); } catch { }
                        try { double distKm = HaversineDistanceMeters(p.Lat, p.Lng, targetLat, targetLon) / 1000.0; FlightData.instance.BeginInvoke(new Action(() => { if (tabLblDistance != null) tabLblDistance.Text = $"Dist: {distKm:F3} km"; })); } catch { }
                    }
                }
                catch { }

                // notify web clients
                try { BroadcastPositions(); } catch { }
                try { UpdateMapDistanceLabel(); } catch { }

                // target track handled in UpdateTargetMarker (avoid duplicating target points here)
            }
            catch (Exception ex)
            {
                Trace.WriteLine("UpdatePlaneMarker error: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private void UpdateTargetMarker(PointLatLng p, bool persist = true)
        {
            try
            {
                try { Trace.WriteLine($"UpdateTargetMarker called with p={p.Lat.ToString(CultureInfo.InvariantCulture)},{p.Lng.ToString(CultureInfo.InvariantCulture)} plane={planeLat.ToString(CultureInfo.InvariantCulture)},{planeLon.ToString(CultureInfo.InvariantCulture)}\nCallStack:\n" + Environment.StackTrace); } catch { }
                // ensure globals reflect this update so broadcasts are accurate
                try { lock (stateLock) { targetLat = p.Lat; targetLon = p.Lng; } } catch { }

                if (overlay == null) return;
                if (targetMarker != null)
                {
                    targetMarker.Position = p;
                }
                else
                {
                    if (targetBmp != null)
                    {
                        targetMarker = new GMarkerGoogle(p, targetBmp) { ToolTipText = "Target" };
                        targetMarker.Offset = new Point(-targetBmp.Width / 2, -targetBmp.Height / 2);
                    }
                    else
                        targetMarker = new GMarkerGoogle(p, GMarkerGoogleType.blue_pushpin) { ToolTipText = "Target" };
                    overlay.Markers.Add(targetMarker);
                    // create or update altitude label marker for target
                    try
                    {
                        string lbl = $"{targetAlt:F1} m";
                        if (targetAltMarker == null)
                        {
                            var txt = new PointLatLng(p.Lat, p.Lng);
                            var m = new GMapMarkerText(txt, lbl) { PixelOffset = new Point(12, 12) };
                            targetAltMarker = m;
                            overlay.Markers.Add(targetAltMarker);
                        }
                        else
                        {
                            targetAltMarker.Position = p;
                            try { (targetAltMarker as GMapMarkerText).Text = lbl; } catch { }
                        }
                    }
                    catch { }
                }
                // update altitude label marker if present
                try
                {
                    string lbl = $"{targetAlt:F1} m";
                    if (targetAltMarker != null)
                    {
                        targetAltMarker.Position = p;
                        try { (targetAltMarker as GMapMarkerText).Text = lbl; } catch { }
                    }
                }
                catch { }
                try { RequestMapRefresh(); } catch { }
                if (persist)
                {
                    try
                    {
                        // ensure globals reflect this update so broadcasts are accurate
                        var s = MissionPlanner.Utilities.Settings.Instance;
                        s["Companion_target_lat"] = p.Lat.ToString(CultureInfo.InvariantCulture);
                        s["Companion_target_lon"] = p.Lng.ToString(CultureInfo.InvariantCulture);
                        try { s.Save(); } catch { }
                    }
                    catch { }
                }

                // update tab UI labels and send gimbal mount command
                try
                {
                    double bearing = ComputeBearing(planeLat, planeLon, p.Lat, p.Lng);
                    try { FlightData.instance.BeginInvoke(new Action(() => {
                        try { if (tabLblTarget != null) tabLblTarget.Text = $"Target: {p.Lat:F6}, {p.Lng:F6}"; } catch { }
                        try { if (tabLblAngle != null) tabLblAngle.Text = double.IsNaN(bearing) ? "Bearing: N/A" : $"Bearing: {bearing:F2}°"; } catch { }
                    })); } catch {
                        try { if (tabLblTarget != null) tabLblTarget.Text = $"Target: {p.Lat:F6}, {p.Lng:F6}"; } catch { }
                        try { if (tabLblAngle != null) tabLblAngle.Text = double.IsNaN(bearing) ? "Bearing: N/A" : $"Bearing: {bearing:F2}°"; } catch { }
                    }
                    if (!double.IsNaN(bearing))
                    {
                        double pitch = ComputePitch(planeLat, planeLon, planeAlt, p.Lat, p.Lng, targetAlt);
                        try { SendGimbal((float)pitch, (float)bearing); } catch { }
                        try { FlightData.instance.BeginInvoke(new Action(() => { if (tabLblPitch != null) tabLblPitch.Text = $"Pitch: {pitch:F2}°"; })); } catch { }
                    }
                }
                catch { }

                // notify web clients
                try { BroadcastPositions(); } catch { }

                // update map distance label
                try { UpdateMapDistanceLabel(); } catch { }

                // ensure FlightData UI target-alt control reflects current value
                try {
                    if (FlightData.instance != null)
                    {
                        FlightData.instance.BeginInvoke(new Action(() => {
                                try { if (nudTargetAlt != null) { var v = (decimal)targetAlt; if (v < nudTargetAlt.Minimum) v = nudTargetAlt.Minimum; if (v > nudTargetAlt.Maximum) v = nudTargetAlt.Maximum; nudTargetAlt.Value = v; } } catch { }
                            try { if (tabLblTargetAlt != null) tabLblTargetAlt.Text = "Alt:"; } catch { }
                        }));
                    }
                } catch { }

                // record target track point (centralized)
                try { RecordTargetPoint(p); } catch { }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("UpdateTargetMarker error: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        // Save current tracks as KML. Called from UI button.
        private void SaveKml()
        {
            try
            {
                using (var sfd = new SaveFileDialog() { Filter = "KML files|*.kml|All files|*.*", DefaultExt = "kml", FileName = "companion_tracks.kml" })
                {
                    Control ownerCtl = Host?.MainForm as Control ?? FlightData.instance as Control;
                    // show dialog synchronously on UI thread if possible
                    DialogResult dr = DialogResult.None;
                    try
                    {
                        if (ownerCtl != null)
                        {
                            ownerCtl.Invoke(new Action(() => { dr = sfd.ShowDialog(ownerCtl); }));
                        }
                        else
                        {
                            dr = sfd.ShowDialog();
                        }
                    }
                    catch
                    {
                        dr = sfd.ShowDialog();
                    }
                    if (dr != DialogResult.OK) return;
                    var path = sfd.FileName;
                    var sb = new StringBuilder();
                    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                    sb.AppendLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\">\n<Document>");

                    // styles
                    sb.AppendLine("<Style id=\"planeLine\"><LineStyle><color>ff0000ff</color><width>3</width></LineStyle></Style>");
                    sb.AppendLine("<Style id=\"targetLine\"><LineStyle><color>ffff0000</color><width>3</width></LineStyle></Style>");

                    // plane track
                    try
                    {
                        if (planeTrackPoints != null && planeTrackPoints.Count > 0)
                        {
                            sb.AppendLine("<Placemark>");
                            sb.AppendLine("<name>Plane Track</name>");
                            sb.AppendLine("<styleUrl>#planeLine</styleUrl>");
                            sb.AppendLine("<LineString>");
                            sb.AppendLine("<tessellate>1</tessellate>");
                            sb.AppendLine("<coordinates>");
                            for (int i = 0; i < planeTrackPoints.Count; i++)
                            {
                                var pt = planeTrackPoints[i];
                                double alt = 0.0;
                                try { if (planeTrackAlts != null && i < planeTrackAlts.Count) alt = planeTrackAlts[i]; } catch { }
                                sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2}\n", pt.Lng, pt.Lat, alt.ToString(CultureInfo.InvariantCulture));
                            }
                            sb.AppendLine("</coordinates>");
                            sb.AppendLine("</LineString>");
                            sb.AppendLine("</Placemark>");
                        }
                    }
                    catch { }

                    // target track
                    try
                    {
                        if (targetTrackPoints != null && targetTrackPoints.Count > 0)
                        {
                            sb.AppendLine("<Placemark>");
                            sb.AppendLine("<name>Target Track</name>");
                            sb.AppendLine("<styleUrl>#targetLine</styleUrl>");
                            sb.AppendLine("<LineString>");
                            sb.AppendLine("<tessellate>1</tessellate>");
                            sb.AppendLine("<coordinates>");
                            for (int i = 0; i < targetTrackPoints.Count; i++)
                            {
                                var pt = targetTrackPoints[i];
                                double alt = 0.0;
                                try { if (targetTrackAlts != null && i < targetTrackAlts.Count) alt = targetTrackAlts[i]; } catch { }
                                sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2}\n", pt.Lng, pt.Lat, alt.ToString(CultureInfo.InvariantCulture));
                            }
                            sb.AppendLine("</coordinates>");
                            sb.AppendLine("</LineString>");
                            sb.AppendLine("</Placemark>");
                        }
                    }
                    catch { }

                    sb.AppendLine("</Document>\n</kml>");

                    try
                    {
                        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("SaveKml write failed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                try { Trace.WriteLine("SaveKml failed: " + ex.Message); } catch { }
            }
        }

        private void ClearTracks()
        {
            try
            {
                planeTrackPoints?.Clear();
                targetTrackPoints?.Clear();
                try { targetTrackAlts?.Clear(); } catch { }
                if (overlay != null)
                {
                    try { if (planeRoute != null) { overlay.Routes.Remove(planeRoute); planeRoute = null; } } catch { }
                    try { if (targetRoute != null) { overlay.Routes.Remove(targetRoute); targetRoute = null; } } catch { }
                    try { RequestMapRefresh(); } catch { }
                }
            }
            catch (Exception ex) { try { Trace.WriteLine("ClearTracks failed: " + ex.Message); } catch { } }
        }

        private bool TracksEnabled()
        {
            try
            {
                if (chkEnableTracks != null) return chkEnableTracks.Checked;
                var st = MissionPlanner.Utilities.Settings.Instance;
                if (st["Companion_tracks_enabled"] != null)
                {
                    bool v; if (bool.TryParse(st["Companion_tracks_enabled"].ToString(), out v)) return v;
                }
            }
            catch { }
            return false;
        }

        private bool TracksPlaneVisible()
        {
            try
            {
                if (chkShowPlaneTrack != null) return chkShowPlaneTrack.Checked;
                var st = MissionPlanner.Utilities.Settings.Instance;
                if (st["Companion_plane_track_visible"] != null)
                {
                    bool v; if (bool.TryParse(st["Companion_plane_track_visible"].ToString(), out v)) return v;
                }
            }
            catch { }
            return true; // default visible
        }

        private bool TracksTargetVisible()
        {
            try
            {
                if (chkShowTargetTrack != null) return chkShowTargetTrack.Checked;
                var st = MissionPlanner.Utilities.Settings.Instance;
                if (st["Companion_target_track_visible"] != null)
                {
                    bool v; if (bool.TryParse(st["Companion_target_track_visible"].ToString(), out v)) return v;
                }
            }
            catch { }
            return true; // default visible
        }

        // Ensure routes are present in overlay in a stable order: plane first, then target.
        private void EnsureRoutesOverlayOrder()
        {
            try
            {
                if (overlay == null) return;
                // remove existing routes to control order
                try { if (planeRoute != null && overlay.Routes.Contains(planeRoute)) overlay.Routes.Remove(planeRoute); } catch { }
                try { if (targetRoute != null && overlay.Routes.Contains(targetRoute)) overlay.Routes.Remove(targetRoute); } catch { }

                // add in fixed order: plane then target
                try { if (TracksPlaneVisible() && planeRoute != null) { try { SetRouteStyle(planeRoute, true); } catch { } overlay.Routes.Add(planeRoute); } } catch { }
                try { if (TracksTargetVisible() && targetRoute != null) { try { SetRouteStyle(targetRoute, false); } catch { } overlay.Routes.Add(targetRoute); } } catch { }

                try { RequestMapRefresh(); } catch { }
            }
            catch { }
        }

        // Global message filter to intercept mouse messages and implement marker drag before map handles them.
        private class GlobalMapMouseFilter : IMessageFilter
        {
            private CompanionPlugin parent;
            public GlobalMapMouseFilter(CompanionPlugin p) { parent = p; }

            private const int WM_MOUSEMOVE = 0x0200;
            private const int WM_LBUTTONDOWN = 0x0201;
            private const int WM_LBUTTONUP = 0x0202;

            public bool PreFilterMessage(ref Message m)
            {
                try
                {
                    if (parent == null || FlightData.instance == null || FlightData.instance.gMapControl1 == null) return false;
                    var control = FlightData.instance.gMapControl1;
                    if (m.Msg == WM_LBUTTONDOWN)
                    {
                        // check if mouse over target marker
                        var pt = Control.MousePosition;
                        var local = control.PointToClient(pt);
                            // check if mouse over target marker
                            if (parent.targetMarker != null)
                            {
                                var markPos = control.FromLatLngToLocal(parent.targetMarker.Position);
                                var dx = markPos.X - local.X;
                                var dy = markPos.Y - local.Y;
                            if (dx * dx + dy * dy <= 20 * 20)
                            {
                                parent._isDraggingTarget = true;
                                try { parent._prevCanDragMap = control.CanDragMap; control.CanDragMap = false; } catch { }
                                return true; // consume
                            }
                            }
                            // check if mouse over plane marker
                            if (parent.planeMarker != null)
                            {
                                var markPos2 = control.FromLatLngToLocal(parent.planeMarker.Position);
                                var dx2 = markPos2.X - local.X;
                                var dy2 = markPos2.Y - local.Y;
                                // use larger hit radius for plane (icon may be larger)
                            if (dx2 * dx2 + dy2 * dy2 <= 30 * 30)
                            {
                                parent._isDraggingPlane = true;
                                try { parent._prevCanDragMap = control.CanDragMap; control.CanDragMap = false; } catch { }
                                return true; // consume
                            }
                            }
                    }
                    else if (m.Msg == WM_MOUSEMOVE)
                    {
                            if (parent._isDraggingTarget)
                            {
                                var pt = Control.MousePosition;
                                var local = control.PointToClient(pt);
                                var p = control.FromLocalToLatLng(local.X, local.Y);
                                try { parent.targetMarker.Position = p; } catch { }
                                try { if (parent.targetAltMarker != null) parent.targetAltMarker.Position = p; } catch { }
                                try { control.Refresh(); } catch { try { parent.RequestMapRefresh(); } catch { } }
                                return true; // consume
                            }
                            else if (parent._isDraggingPlane)
                            {
                                var pt2 = Control.MousePosition;
                                var local2 = control.PointToClient(pt2);
                                var p2 = control.FromLocalToLatLng(local2.X, local2.Y);
                                try { parent.planeMarker.Position = p2; } catch { }
                                try { if (parent.planeAltMarker != null) parent.planeAltMarker.Position = p2; } catch { }
                                try { control.Refresh(); } catch { try { parent.RequestMapRefresh(); } catch { } }
                                return true;
                            }
                    }
                    else if (m.Msg == WM_LBUTTONUP)
                    {
                            if (parent._isDraggingTarget)
                            {
                                parent._isDraggingTarget = false;
                                var pt = Control.MousePosition;
                                var local = control.PointToClient(pt);
                                var p = control.FromLocalToLatLng(local.X, local.Y);
                                try { parent.UpdateTargetMarker(p, true); } catch { }
                                try { control.CanDragMap = parent._prevCanDragMap; } catch { }
                                return true; // consume
                            }
                            else if (parent._isDraggingPlane)
                            {
                                parent._isDraggingPlane = false;
                                var pt2 = Control.MousePosition;
                                var local2 = control.PointToClient(pt2);
                                var p2 = control.FromLocalToLatLng(local2.X, local2.Y);
                                try { parent.UpdatePlaneMarker(p2, true); } catch { }
                                try { control.CanDragMap = parent._prevCanDragMap; } catch { }
                                return true;
                            }
                    }
                }
                catch { }
                return false;
            }
        }

        private void SetRouteStyle(GMapRoute route, bool isPlane)
        {
            try
            {
                if (route == null) return;
                if (isPlane)
                {
                    try { route.Stroke = new Pen(Color.FromArgb(120, Color.Green), 4f); } catch { }
                }
                else
                {
                    try { route.Stroke = new Pen(Color.FromArgb(120, Color.Red), 4f); } catch { }
                }
            }
            catch { }
        }

        // Record a target track point (centralized). Deduplicates and updates overlay/route.
        private void RecordTargetPoint(PointLatLng p)
        {
            try
            {
                // respect global enable checkbox: do not record when tracks disabled
                try { if (!TracksEnabled()) return; } catch { }
                if (targetTrackPoints == null) targetTrackPoints = new List<PointLatLng>();
                bool addt = false;
                if (targetTrackPoints.Count == 0) addt = true;
                else
                {
                    var lastt = targetTrackPoints[targetTrackPoints.Count - 1];
                    double dt = HaversineDistanceMeters(lastt.Lat, lastt.Lng, p.Lat, p.Lng);
                    if (dt >= 1.0) addt = true;
                }
                if (!addt) return;

                // avoid recording a target point identical to the current plane position
                try
                {
                    double eps = 1e-6;
                    if (!double.IsNaN(planeLat) && !double.IsNaN(planeLon) && Math.Abs(p.Lat - planeLat) < eps && Math.Abs(p.Lng - planeLon) < eps)
                    {
                        try { Trace.WriteLine("Skipping TargetTrack add: identical to plane position"); } catch { }
                        return;
                    }
                }
                catch { }

                // avoid consecutive duplicate target points
                try
                {
                    double eps = 1e-6;
                    if (targetTrackPoints.Count > 0)
                    {
                        var lastt = targetTrackPoints[targetTrackPoints.Count - 1];
                        if (Math.Abs(lastt.Lat - p.Lat) < eps && Math.Abs(lastt.Lng - p.Lng) < eps)
                        {
                            return;
                        }
                    }
                }
                catch { }

                targetTrackPoints.Add(p);
                try { if (targetTrackAlts == null) targetTrackAlts = new List<double>(); targetTrackAlts.Add(double.IsNaN(targetAlt) ? 0.0 : targetAlt); } catch { }
                try { Trace.WriteLine($"TargetTrack add: {p.Lat.ToString(CultureInfo.InvariantCulture)},{p.Lng.ToString(CultureInfo.InvariantCulture)}"); } catch { }

                if (targetRoute == null) targetRoute = new GMapRoute(targetTrackPoints, "targetTrack");
                targetRoute.Points.Clear();
                targetRoute.Points.AddRange(targetTrackPoints);
                try { try { SetRouteStyle(targetRoute, false); } catch { } } catch { }

                if (overlay != null)
                {
                    if (TracksTargetVisible())
                    {
                        try { if (overlay.Routes.Contains(targetRoute)) overlay.Routes.Remove(targetRoute); } catch { }
                        try { try { SetRouteStyle(targetRoute, false); } catch { } overlay.Routes.Add(targetRoute); } catch { }
                    }
                    else
                    {
                        try { if (overlay.Routes.Contains(targetRoute)) overlay.Routes.Remove(targetRoute); } catch { }
                    }
                    try { EnsureRoutesOverlayOrder(); } catch { }
                    try { RequestMapRefresh(); } catch { }
                }
            }
            catch { }
        }

        private double ComputeBearing(double lat1deg, double lon1deg, double lat2deg, double lon2deg)
        {
            try
            {
                if (double.IsNaN(lat1deg) || double.IsNaN(lon1deg) || double.IsNaN(lat2deg) || double.IsNaN(lon2deg))
                    return double.NaN;
                double lat1 = ToRad(lat1deg);
                double lat2 = ToRad(lat2deg);
                double dLon = ToRad(lon2deg - lon1deg);

                double y = Math.Sin(dLon) * Math.Cos(lat2);
                double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
                double brng = Math.Atan2(y, x);
                double brngDeg = (ToDeg(brng) + 360.0) % 360.0;
                return brngDeg;
            }
            catch { return double.NaN; }
        }

        private double ToRad(double deg) { return deg * Math.PI / 180.0; }
        private double ToDeg(double rad) { return rad * 180.0 / Math.PI; }

        private double NormalizeAngle(double deg)
        {
            // normalize to (-180, 180]
            double a = (deg + 180.0) % 360.0;
            if (a < 0) a += 360.0;
            return a - 180.0;
        }

        private double ComputePitch(double lat1deg, double lon1deg, double alt1m, double lat2deg, double lon2deg, double alt2m)
        {
            try
            {
                if (double.IsNaN(lat1deg) || double.IsNaN(lon1deg) || double.IsNaN(lat2deg) || double.IsNaN(lon2deg) || double.IsNaN(alt1m) || double.IsNaN(alt2m))
                    return double.NaN;

                double horiz = HaversineDistanceMeters(lat1deg, lon1deg, lat2deg, lon2deg);
                double delta = alt2m - alt1m; // target minus plane
                double pitchRad = Math.Atan2(delta, horiz);
                double pitchDeg = ToDeg(pitchRad);
                return pitchDeg;
            }
            catch { return double.NaN; }
        }

        private double HaversineDistanceMeters(double lat1deg, double lon1deg, double lat2deg, double lon2deg)
        {
            const double R = 6371000.0; // Earth radius in meters
            double phi1 = ToRad(lat1deg);
            double phi2 = ToRad(lat2deg);
            double dphi = ToRad(lat2deg - lat1deg);
            double dlambda = ToRad(lon2deg - lon1deg);
            double a = Math.Sin(dphi / 2.0) * Math.Sin(dphi / 2.0) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dlambda / 2.0) * Math.Sin(dlambda / 2.0);
            double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private void UpdateMapDistanceLabel()
        {
            try
            {
                if (mapDistanceLabel == null) return;
                if (double.IsNaN(planeLat) || double.IsNaN(planeLon) || double.IsNaN(targetLat) || double.IsNaN(targetLon))
                {
                    try {
                        FlightData.instance?.BeginInvoke(new Action(() => {
                            if (mapDistanceLabel != null) {
                                mapDistanceLabel.Text = "";
                                mapDistanceLabel.Visible = false;
                                mapDistanceLabel.ForeColor = Color.Black;
                            }
                        }));
                    } catch {
                        if (mapDistanceLabel != null) {
                            mapDistanceLabel.Text = "";
                            mapDistanceLabel.Visible = false;
                            mapDistanceLabel.ForeColor = Color.Black;
                        }
                    }
                    return;
                }
                double meters = HaversineDistanceMeters(planeLat, planeLon, targetLat, targetLon);
                double km = meters / 1000.0;
                string txt = $"Dist: {km:F3} km";
                try {
                    FlightData.instance?.BeginInvoke(new Action(() => {
                        if (mapDistanceLabel != null) {
                            mapDistanceLabel.Text = txt;
                            mapDistanceLabel.Visible = true;
                            mapDistanceLabel.ForeColor = Color.Black;
                        }
                    }));
                } catch {
                    if (mapDistanceLabel != null) {
                        mapDistanceLabel.Text = txt;
                        mapDistanceLabel.Visible = true;
                        mapDistanceLabel.ForeColor = Color.Black;
                    }
                }
            }
            catch { }
        }


        private void SendGimbal(float pitchDeg, float yawDeg)
        {
                

            try
            {
                if (MainV2.comPort == null || MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen) return;

                // respect persisted enable/disable toggle (default OFF when missing)
                try {
                    var st_chk = MissionPlanner.Utilities.Settings.Instance;
                    bool enabled = false;
                    if (st_chk["Companion_gimbal_enabled"] != null) {
                        bool tmp; if (bool.TryParse(st_chk["Companion_gimbal_enabled"].ToString(), out tmp)) enabled = tmp;
                    }
                    if (!enabled) return;
                } catch { return; }

                float pitch = (float)pitchDeg;
                const float roll = 0.0f;
                // normalize yaw into [0,360)
                double yawd = ((yawDeg % 360.0) + 360.0) % 360.0;
                float yaw = (float)yawd;
                const float p4 = 0.0f;
                const float p5 = 0.0f;
                const float p6 = 0.0f;
                

                // compute PWM mapping for display and possible RC override
                // map to PWM using constants (center ± span)
                double pwmPitchD = GIMBAL_PWM_CENTER + (double)pitch * GIMBAL_PWM_PITCH_SCALE;
                double yawNormForPwm = NormalizeAngle(yawd);
                double pwmYawD = GIMBAL_PWM_CENTER + yawNormForPwm * GIMBAL_PWM_YAW_SCALE;
                int pwmPitch = (int)Math.Round(pwmPitchD);
                int pwmYaw = (int)Math.Round(pwmYawD);
                pwmPitch = Math.Max(GIMBAL_PWM_MIN, Math.Min(GIMBAL_PWM_MAX, pwmPitch));
                pwmYaw = Math.Max(GIMBAL_PWM_MIN, Math.Min(GIMBAL_PWM_MAX, pwmYaw));
                

                // Determine whether Use RC checkbox is checked
                bool useRc = false;
                try { if (chkUseRcControl != null) useRc = chkUseRcControl.Checked; else { var s = MissionPlanner.Utilities.Settings.Instance; if (s["Companion_gimbal_rc"] != null) bool.TryParse(s["Companion_gimbal_rc"].ToString(), out useRc); } } catch { }

                if (!useRc)
                {
                    // Use Gimbal Manager: send MAV_CMD_DO_GIMBAL_MANAGER_PITCHYAW (1000)
                    try
                    {
                        var st = MissionPlanner.Utilities.Settings.Instance;
                        float gm1 = float.NaN, gm2 = float.NaN, gm3 = float.NaN, gm4 = float.NaN, gm5 = 16f, gm6 = 0f, gm7 = 0f;
                        // Use computed pitch/yaw angles; leave rates unset (NaN)
                        gm1 = pitch; // pitch angle in deg (positive is up)
                        gm2 = yaw;   // yaw angle in deg (positive is clockwise)
                        gm3 = float.NaN; gm4 = float.NaN;
                        // Force flags to 16 (locked) and keep stored setting consistent
                        try { st["Companion_gimbal_manager_flags"] = "16"; try { st.Save(); } catch { } } catch { }
                        float.TryParse(st["Companion_gimbal_manager_id"]?.ToString() ?? "0", out gm7);

                        // send COMMAND_LONG with command=1000 and params gm1..gm7
                        MainV2.comPort.doCommand((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent,
                            (MAVLink.MAV_CMD)1000,
                            gm1, gm2, gm3, gm4, gm5, gm6, gm7, false);
                        return;
                    }
                    catch { /* fall through */ }
                }
                else
                {
                    // Use RC selected: send DO_MOUNT_CONTROL with RC_TARGETING mode (3)
                    try
                    {
                        MainV2.comPort.doCommand((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent,
                            MAVLink.MAV_CMD.DO_MOUNT_CONTROL,
                            pitch, roll, yaw, p4, p5, p6, 3f, false);
                        return;
                    }
                    catch { /* fall through */ }
                }

                
                return;
            }
            catch (Exception ex)
            {
                try { Trace.WriteLine("Companion send failed: " + ex.Message); } catch { }
            }
        }

        private string GetWebUIHtml(int port)
        {
                        // Leaflet map UI: tap to set plane/target and live updates via WebSocket
                        return @"<!doctype html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width,initial-scale=1'>
    <title>MissionPlanner Companion - Map</title>
    <link rel='stylesheet' href='/static/leaflet.css' />
    <style>
        html,body{height:100%;margin:0;padding:0;font-family:Arial;overscroll-behavior: none;}
        /* Map height leaves room for the fixed control bar */
        #map{height:100%;width:100%;overscroll-behavior: none;}
        /* Fixed controls at bottom so they are always visible on mobile */
        .controls{position:fixed;left:0;right:0;bottom:0;height:64px;padding:8px 10px;display:flex;align-items:center;gap:8px;box-shadow:0 -2px 6px rgba(0,0,0,0.08);z-index:1000;touch-action:pan-x;-ms-touch-action:pan-x}
        .btn{padding:8px 10px;border:1px solid #888;border-radius:4px;background:#f0f0f0}
        .btn.active{background:#cce5ff}
        .info{white-space:pre-wrap;margin-left:8px;flex:1;overflow:hidden;text-overflow:ellipsis}
        /* fullscreen icon button */
        .fullscreen-btn{position:fixed;right:8px;top:8px;z-index:1002;padding:8px;border:1px solid #888;border-radius:4px;background:#f0f0f0;display:flex;align-items:center;justify-content:center}
        #altControl{touch-action:pan-x;-ms-touch-action:pan-x}
        /* ensure map content isn't covered by the fixed controls */
        @media (max-width:540px) {
            .controls { padding:6px 8px; }
        }

        /* centered modal for layer selection */
        #layersModalBackdrop{ display:none; position:fixed; left:0; top:0; right:0; bottom:0; background:rgba(0,0,0,0.35); z-index:1500; align-items:center; justify-content:center; }
        #layersModal{ background:#fff; padding:12px 14px; border-radius:8px; box-shadow:0 6px 20px rgba(0,0,0,0.25); min-width:260px; max-width:90%; display:flex; flex-direction:column; gap:8px; }
        #layersModal .row{ display:flex; align-items:center; gap:8px; }
        #layersModal .row label{ font-size:13px; color:#333; }
        #layersModalClose{ position:absolute; top:8px; right:12px; background:transparent;border:0;font-size:18px; cursor:pointer }
    </style>
</head>
<body>
    <div id='map'></div>
    <div class='controls'>
            <div style='display:flex;align-items:center;gap:8px'>
            <button id='sel_plane' class='btn active'>Set Plane</button>
            <button id='sel_target' class='btn'>Set Target</button>
            <button id='center_plane' class='btn'>Center on Plane</button>
            <button id='center_target' class='btn'>Center on Target</button>
            </div>
        <div id='statusWrap' style='margin-left:10px;display:flex;align-items:center;gap:6px'>
            <div id='wsStatus' title='connection status' style='width:12px;height:12px;border-radius:50%;background:#f44;box-shadow:0 0 4px rgba(0,0,0,0.2)'></div>
            <button id='layersToggle' class='btn' style='padding:4px 6px;font-size:12px'>Layers</button>
        </div>

        <div id='layersModalBackdrop'>
            <div id='layersModal'>
                <button id='layersModalClose' title='Close'>&times;</button>
                <div class='row'><label>Base:</label><select id='baseProvider' class='btn'></select></div>
                <div class='row'><label>Overlay:</label><select id='overlayProvider' class='btn'></select></div>
            </div>
        </div>
        <div class='info' id='posinfo'>Loading positions...</div>
    </div>

    <!-- display target altitude at top -->
    <div id='topAlt' style='position:fixed;top:8px;left:50%;transform:translateX(-50%);z-index:1001;background:rgba(255,255,255,0.95);padding:6px 10px;border-radius:6px;font-weight:700;'>Target Alt: 0 m</div>

    <!-- vertical altitude slider on right side (longer) -->
    <div id='altControl' style='position:fixed;right:8px;top:50%;transform:translateY(-50%);width:80px;display:flex;flex-direction:column;align-items:center;z-index:1000'>
        <input id='altSlider' type='range' min='-100' max='5000' step='1' value='0' style='transform:rotate(-90deg);width:280px;height:36px;'/>
    </div>

    <!-- fullscreen icon button (always visible) -->
    <button id='fullscreenBtn' class='fullscreen-btn' aria-label='Fullscreleafleten'>
        <svg width='20' height='20' viewBox='0 0 24 24' fill='none' xmlns='http://www.w3.org/2000/svg'>
            <path d='M7 14H5v6h6v-2H7v-4zM7 4v6h2V6h4V4H7zM17 4h-4v2h4v4h2V4h-2zM17 14h2v6h-6v-2h4v-4z' fill='#333'/>
        </svg>
    </button>

    <script src='/static/leaflet.js'></script>
    <script>
        const map = L.map('map', { doubleClickZoom: false, tap: false }).setView([0,0],2);
        // dynamic provider selection: client will fetch tiles directly from public providers
        var satLayer = null, hybridLayer = null;
        const providerTemplates = {
            'GoogleSatelliteMap': { url: 'https://mt{s}.google.com/vt/lyrs=s&x={x}&y={y}&z={z}', maxZoom: 20, subdomains: '0123' },
            'GoogleHybridMap': { url: 'https://mt{s}.google.com/vt/lyrs=y&x={x}&y={y}&z={z}', maxZoom: 20, subdomains: '0123', pane: 'overlayPane' },
            'OpenStreetMap': { url: 'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', maxZoom: 19, subdomains: 'abc' },
            'EsriWorldImagery': { url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', maxZoom: 19, subdomains: '' },
            'CartoDB_Positron': { url: 'https://cartodb-basemaps-{s}.global.ssl.fastly.net/light_all/{z}/{x}/{y}.png', maxZoom: 19, subdomains: 'abcd' },
            'Stamen_Toner': { url: 'https://stamen-tiles.a.ssl.fastly.net/toner/{z}/{x}/{y}.png', maxZoom: 20, subdomains: 'abcd' },
            'Stamen_Terrain': { url: 'https://stamen-tiles.a.ssl.fastly.net/terrain/{z}/{x}/{y}.jpg', maxZoom: 18, subdomains: 'abcd' },
            'OpenTopoMap': { url: 'https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png', maxZoom: 17, subdomains: 'abc' }
        };

        function setLayers(base, overlay){
            try {
                if (satLayer) { try { map.removeLayer(satLayer); } catch(e){} satLayer = null; }
                if (hybridLayer) { try { map.removeLayer(hybridLayer); } catch(e){} hybridLayer = null; }
                if (base && providerTemplates[base]) {
                    var t = providerTemplates[base];
                    satLayer = L.tileLayer(t.url, { maxZoom: t.maxZoom || 19, attribution: '', subdomains: t.subdomains || 'abc' }).addTo(map);
                }
                if (overlay && providerTemplates[overlay]) {
                    var t2 = providerTemplates[overlay];
                    hybridLayer = L.tileLayer(t2.url, { maxZoom: t2.maxZoom || 19, attribution: '', pane: 'overlayPane' });
                    hybridLayer.addTo(map);
                }
            } catch(e) { console.log('setLayers error', e); }
        }

        // populate selectors with a fixed set of public providers
        try {
            try {
                const baseSel = document.getElementById('baseProvider');
                const overSel = document.getElementById('overlayProvider');
                const keys = Object.keys(providerTemplates);
                // add 'None' option
                var noneBase = document.createElement('option'); noneBase.value = ''; noneBase.text = 'None'; baseSel.appendChild(noneBase); baseSel.insertBefore(noneBase, baseSel.firstChild);
                var noneOver = document.createElement('option'); noneOver.value = ''; noneOver.text = 'None'; overSel.appendChild(noneOver); overSel.insertBefore(noneOver, overSel.firstChild);
                keys.forEach(p=>{ var o=document.createElement('option'); o.value=p; o.text=p; baseSel.appendChild(o); var o2=document.createElement('option'); o2.value=p; o2.text=p; overSel.appendChild(o2); });
                baseSel.value = 'GoogleSatelliteMap';
                overSel.value = 'GoogleHybridMap';
                setLayers(baseSel.value, overSel.value);
                baseSel.addEventListener('change', ()=> setLayers(baseSel.value, overSel.value));
                overSel.addEventListener('change', ()=> setLayers(baseSel.value, overSel.value));
            } catch(e) { console.log('providers populate error', e); setLayers(null,null); }
        } catch(e){ setLayers(null,null); }

        const planeOpts = {radius:8,color:'red',fillColor:'red',fillOpacity:0.9};
        const targetOpts = {radius:8,color:'blue',fillColor:'blue',fillOpacity:0.9};
        let planeMarker = null, targetMarker = null;
        let targetLat = NaN, targetLon = NaN;
        let selected = 'plane';
        const wsStatus = document.getElementById('wsStatus');
        const layersToggle = document.getElementById('layersToggle');
        const layersFrame = document.getElementById('layersFrame');
        function setWsStatus(color, tip) { try { if (wsStatus) { wsStatus.style.background = color; if (tip) wsStatus.title = tip; } } catch (e) { } }
        try { if (layersToggle) layersToggle.addEventListener('click', function(){ try { var bd = document.getElementById('layersModalBackdrop'); if(!bd) return; bd.style.display = (bd.style.display==='flex') ? 'none' : 'flex'; } catch(e){} }); } catch(e){}
        try { var layersModalBackdrop = document.getElementById('layersModalBackdrop'); if (layersModalBackdrop) layersModalBackdrop.addEventListener('click', function(ev){ try { if (ev.target === layersModalBackdrop) layersModalBackdrop.style.display='none'; } catch(e){} }); } catch(e){}
        try { var layersModalClose = document.getElementById('layersModalClose'); if (layersModalClose) layersModalClose.addEventListener('click', function(){ try{ var bd = document.getElementById('layersModalBackdrop'); if(bd) bd.style.display='none'; } catch(e){} }); } catch(e){}
        const posinfo = document.getElementById('posinfo');

        function renderPositions(o){
            posinfo.innerText = 'Plane: ' + o.plane.lat + ', ' + o.plane.lon + '\nTarget: ' + o.target.lat + ', ' + o.target.lon;
            // update stored target coords
            if(o.target && !isNaN(o.target.lat)){
                targetLat = o.target.lat; targetLon = o.target.lon;
            }
            // update slider with target altitude if provided
                try {
                if(o.target){
                    const s = document.getElementById('altSlider');
                    try {
                        if (s && typeof o.target.min !== 'undefined') s.min = o.target.min;
                        if (s && typeof o.target.max !== 'undefined') s.max = o.target.max;
                        if (s && typeof o.target.alt !== 'undefined') s.value = o.target.alt;
                    } catch(e){}
                    try { var ta = document.getElementById('topAlt'); if(ta && typeof o.target.alt !== 'undefined') ta.innerText = 'Target Alt: ' + Math.round(o.target.alt) + ' m'; } catch(e){}
                }
            } catch(e){}
            if(!isNaN(o.plane.lat)){
                setMarker('plane', o.plane.lat, o.plane.lon, false);
            }
            if(!isNaN(o.target.lat)){
                setMarker('target', o.target.lat, o.target.lon, false);
            }
        }

        function setMarker(type, lat, lon, doPost){
            if(type==='plane'){
                if(planeMarker) planeMarker.setLatLng([lat,lon]); else planeMarker = L.circleMarker([lat,lon], planeOpts).addTo(map).bindPopup('Plane');
            } else {
                if(targetMarker) targetMarker.setLatLng([lat,lon]); else targetMarker = L.circleMarker([lat,lon], targetOpts).addTo(map).bindPopup('Target');
            }
            if(doPost){
                const url = type==='plane' ? '/plane' : '/target';
                const body = { lat: lat, lon: lon };
                try { body.alt = parseFloat(document.getElementById('altSlider').value); } catch (e) { body.alt = 0; }
                fetch(url,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)}).catch(e=>console.log(e));
            }
        }

        map.on('click', function(e){
            const lat = e.latlng.lat, lon = e.latlng.lng;
            setMarker(selected, lat, lon, true);
            try { if(posinfo) posinfo.innerText = selected + ' set: ' + lat.toFixed(6) + ', ' + lon.toFixed(6); } catch(e){}
        });

        document.getElementById('sel_plane').onclick = function(){ selected='plane'; this.classList.add('active'); document.getElementById('sel_target').classList.remove('active'); };
        document.getElementById('sel_target').onclick = function(){ selected='target'; this.classList.add('active'); document.getElementById('sel_plane').classList.remove('active'); };
        document.getElementById('center_plane').onclick = function(){ if(planeMarker){ map.setView(planeMarker.getLatLng(),12); } };
        document.getElementById('center_target').onclick = function(){ if(targetMarker){ map.setView(targetMarker.getLatLng(),12); } };

        // fullscreen button: requires user gesture on mobile browsers
        try {
            var fsBtn = document.getElementById('fullscreenBtn');
            if (fsBtn) fsBtn.onclick = function(){
                var el = document.documentElement;
                try {
                    var isFS = !!(document.fullscreenElement || document.webkitFullscreenElement || document.msFullscreenElement);
                    if (isFS)
                    {
                        if (document.exitFullscreen) document.exitFullscreen(); else if (document.webkitExitFullscreen) document.webkitExitFullscreen(); else if (document.msExitFullscreen) document.msExitFullscreen();
                    }
                    else
                    {
                        if (el.requestFullscreen) el.requestFullscreen(); else if (el.webkitRequestFullscreen) el.webkitRequestFullscreen(); else if (el.msRequestFullscreen) el.msRequestFullscreen();
                    }
                } catch(e) { console.log('fullscreen toggle failed', e); }
            };
        } catch(e) { }

        // adjust map height and controls visibility when entering/exiting fullscreen
        function onFullscreenChange(){
            try{
                var mapEl = document.getElementById('map');
                var controls = document.querySelector('.controls');
                var isFS = !!(document.fullscreenElement || document.webkitFullscreenElement || document.msFullscreenElement);
                if (mapEl){ mapEl.style.height = isFS ? '100vh' : 'calc(100vh - 64px)'; }
                if (controls){ controls.style.display = isFS ? 'none' : 'flex'; }
                // ensure fullscreen button stays visible
                try { var fsb = document.getElementById('fullscreenBtn'); if (fsb) fsb.style.display = 'flex'; } catch(e) {}
            } catch(e) { }
        }
        document.addEventListener('fullscreenchange', onFullscreenChange);
        document.addEventListener('webkitfullscreenchange', onFullscreenChange);

        // fetch initial positions with retry/backoff
        (function(){
            let fpBackoff = 1000;
                function fetchPositions(){
                fetch('/positions').then(r=>r.json()).then(d=>{ renderPositions(d); try{ setWsStatus('green','HTTP OK'); }catch(e){} fpBackoff = 1000; }).catch(e=>{ try{ setWsStatus('red','failed to load positions'); }catch(ex){} setTimeout(fetchPositions, fpBackoff); fpBackoff = Math.min(fpBackoff*2, 30000); });
            }
            fetchPositions();
        })();

        // websocket for live updates with automatic reconnect
        (function(){
            const proto = (location.protocol==='https:') ? 'wss://' : 'ws://';
            let ws = null;
            let reconnectBackoff = 1000;
            function connect(){
                try{
                    ws = new WebSocket(proto + location.hostname + ':' + location.port + '/ws');
                    ws.onopen = ()=>{ try{ setWsStatus('green','ws connected'); }catch(e){} reconnectBackoff = 1000; };
                    ws.onmessage = (ev)=>{ try{ const msg = JSON.parse(ev.data); if(msg.type==='positions') renderPositions(msg.payload); }catch(e){} };
                    ws.onclose = ()=>{ try{ setWsStatus('red','ws closed'); }catch(e){} setTimeout(connect, reconnectBackoff); reconnectBackoff = Math.min(reconnectBackoff*2, 30000); };
                    ws.onerror = ()=>{ try{ ws.close(); }catch(e){} };
                }catch(e){ console.log('ws init failed', e); setTimeout(connect, reconnectBackoff); reconnectBackoff = Math.min(reconnectBackoff*2, 30000); }
            }
            connect();
        })();

        // slider handler: send updated target altitude when moved (throttle to once per 200ms)
        try{
            const slider = document.getElementById('altSlider');
            if(slider){
                let lastSliderSend = 0;
                let sliderSendTimer = null;
                const THROTTLE_MS = 200;

                function sendSliderValue(v){
                    if(isNaN(targetLat) || isNaN(targetLon)) return;
                    fetch('/target',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({lat:targetLat,lon:targetLon,alt:v})}).catch(e=>console.log(e));
                }

                slider.addEventListener('input', function(ev){
                    const v = parseFloat(this.value);
                    try{ var ta = document.getElementById('topAlt'); if(ta) ta.innerText = 'Target Alt: ' + Math.round(v) + ' m'; } catch(e){}

                    // throttle sends to approx once per THROTTLE_MS
                    const now = Date.now();
                    const since = now - lastSliderSend;
                    if (since >= THROTTLE_MS) {
                        // send immediately
                        lastSliderSend = now;
                        try { sendSliderValue(v); } catch(e){ console.log(e); }
                        // clear any pending timer
                        if (sliderSendTimer) { clearTimeout(sliderSendTimer); sliderSendTimer = null; }
                    } else {
                        // schedule a send at the earliest allowed time with the latest value
                        if (sliderSendTimer) clearTimeout(sliderSendTimer);
                        sliderSendTimer = setTimeout(function(){
                            lastSliderSend = Date.now();
                            try { sendSliderValue(parseFloat(slider.value)); } catch(e){ console.log(e); }
                            sliderSendTimer = null;
                        }, THROTTLE_MS - since);
                    }
                });
            }
        }catch(e){ }

    </script>
</body>
</html>";
        }

        private Bitmap CreateCircleIcon(Color c, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                int pad = Math.Max(2, size / 8);
                using (var b = new SolidBrush(c))
                    g.FillEllipse(b, pad, pad, size - pad * 2, size - pad * 2);
            }
            return bmp;
        }

        private void StopServer()
        {
            try
            {
                running = false;
                try { listenerCts?.Cancel(); } catch (Exception ex) { Trace.WriteLine("StopServer cancel token failed: " + ex.Message); }
                try { listener.Stop(); } catch (Exception ex) { Trace.WriteLine("StopServer listener.Stop failed: " + ex.Message); }
                try { listener.Close(); } catch (Exception ex) { Trace.WriteLine("StopServer listener.Close failed: " + ex.Message); }
                try { listenerThread?.Join(500); } catch (Exception ex) { Trace.WriteLine("StopServer join failed: " + ex.Message); }
                // stop and wait for all send queues to finish (short timeout)
                List<Task> stopTasks = new List<Task>();
                lock (socketsLock)
                {
                    foreach (var sq in sendQueues)
                    {
                        try { stopTasks.Add(sq.StopAsync()); } catch (Exception ex) { Trace.WriteLine("StopServer stop sendqueue failed: " + ex.Message); }
                    }
                    sendQueues.Clear();
                }
                try
                {
                    if (stopTasks.Count > 0)
                    {
                        Task.WaitAll(stopTasks.ToArray(), 1000);
                    }
                }
                catch (Exception ex) { Trace.WriteLine("StopServer wait for sendqueues failed: " + ex.Message); }
                // remove file trace listener if present
                try
                {
                    if (fileTraceListener != null)
                    {
                        try { Trace.WriteLine("CompanionPlugin log stopped: " + DateTime.UtcNow.ToString("o")); } catch { }
                        try { Trace.Listeners.Remove(fileTraceListener); } catch { }
                        try { fileTraceListener.Close(); } catch { }
                        fileTraceListener = null;
                    }
                }
                catch { }
            }
            catch (Exception ex) { Trace.WriteLine("StopServer error: " + ex.Message); }
        }

        public override bool Exit()
        {
            try
            {
                StopServer();
                if (overlay != null && FlightData.instance != null && FlightData.instance.gMapControl1 != null)
                {
                    try {
                        try { if (planeRoute != null) { overlay.Routes.Remove(planeRoute); planeRoute = null; } } catch { }
                        try { if (targetRoute != null) { overlay.Routes.Remove(targetRoute); targetRoute = null; } } catch { }
                        try { planeTrackPoints?.Clear(); } catch { }
                        try { targetTrackPoints?.Clear(); } catch { }
                        try { planeTrackAlts?.Clear(); } catch { }
                        try { targetTrackAlts?.Clear(); } catch { }
                    } catch { }
                    try { FlightData.instance.gMapControl1.Overlays.Remove(overlay); } catch { }
                    // per-control map handlers removed; global filter detached elsewhere
                    try { if (_markerHandlersAttached) { FlightData.instance.gMapControl1.OnMarkerEnter -= GMap_OnMarkerEnter; FlightData.instance.gMapControl1.OnMarkerLeave -= GMap_OnMarkerLeave; _markerHandlersAttached = false; } } catch { }
                    try { if (_globalMapFilter != null) { try { Application.RemoveMessageFilter(_globalMapFilter); } catch { } _globalMapFilter = null; } } catch { }
                    try { if (mapDistanceLabel != null && FlightData.instance != null && FlightData.instance.gMapControl1 != null) { try { FlightData.instance.gMapControl1.Controls.Remove(mapDistanceLabel); } catch { } try { mapDistanceLabel.Dispose(); } catch { } mapDistanceLabel = null; } } catch { }
                    overlay = null;
                }
                try { if (planeBmp != null) { planeBmp.Dispose(); planeBmp = null; } } catch { }
                try { if (targetBmp != null) { targetBmp.Dispose(); targetBmp = null; } } catch { }
            }
            catch { }
            return true;
        }
    }

    // Custom plane marker class so we can scale the plane icon size and rotate with heading
    // Simple text marker to render a label at a geographic position
    public class GMapMarkerText : GMap.NET.WindowsForms.GMapMarker
    {
        public string Text = "";
        public Font Font = new Font("Segoe UI", 10, FontStyle.Bold);
        public Color ForeColor = Color.White;
        public Color BackColor = Color.FromArgb(160, 0, 0, 0);
        // pixel offset from the geographic point where the text is drawn
        public Point PixelOffset = new Point(0, 0);

        public GMapMarkerText(PointLatLng p, string text) : base(p)
        {
            this.Text = text;
        }

        public override void OnRender(IGraphics g)
        {
            try
            {
                if (string.IsNullOrEmpty(Text)) return;
                using (var bmp = new Bitmap(1, 1))
                using (var gr = Graphics.FromImage(bmp))
                {
                    var sz = gr.MeasureString(Text, Font);
                    var w = (int)Math.Ceiling(sz.Width) + 6;
                    var h = (int)Math.Ceiling(sz.Height) + 4;
                    using (var off = new Bitmap(w, h))
                    using (var g2 = Graphics.FromImage(off))
                    {
                        g2.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var b = new SolidBrush(BackColor))
                            g2.FillRectangle(b, 0, 0, w, h);
                        using (var sf = new StringFormat() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
                        using (var br = new SolidBrush(ForeColor))
                            g2.DrawString(Text, Font, br, new RectangleF(3, 0, w - 6, h), sf);

                        // draw the composed image to the map
                        var img = off;
                        // apply pixel offset (allows placing the label to the right/below the icon)
                        try { g.DrawImage(img, LocalPosition.X + PixelOffset.X, LocalPosition.Y + PixelOffset.Y, w, h); } catch { }
                    }
                }
            }
            catch { }
        }
    }
    public class GMapMarkerPlaneCustom : GMap.NET.WindowsForms.GMapMarker
    {
        private Bitmap icon;
        public float heading = 0f;

        public GMapMarkerPlaneCustom(PointLatLng p, float heading, int size = 70) : base(p)
        {
            this.heading = heading;
            try
            {
                // use same resource images as ADSB plane but scaled to requested size
                icon = new Bitmap(global::MissionPlanner.Maps.Resources.planeicon3, new Size(size, size));
                Size = icon.Size;
                Offset = new Point(Size.Width / -2, Size.Height / -2);
            }
            catch
            {
                icon = null;
                Size = new System.Drawing.Size(size, size);
                Offset = new Point(Size.Width / -2, Size.Height / -2);
            }
        }

        public override void OnRender(IGraphics g)
        {
            var temp = g.Transform;
            g.TranslateTransform(LocalPosition.X - Offset.X, LocalPosition.Y - Offset.Y);
            g.RotateTransform(-Overlay.Control.Bearing);
            try { g.RotateTransform(heading); } catch (Exception ex) { Trace.WriteLine("OnRender rotate heading failed: " + ex.Message); }
            try
            {
                if (icon != null)
                    g.DrawImageUnscaled(icon, icon.Width / -2, icon.Height / -2);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("GMapMarkerPlaneCustom.OnRender error: " + ex.Message + "\n" + ex.StackTrace);
            }
            try
            {
                // draw gimbal cone using CurrentState campoint values if available
                float gimbalYaw = float.NaN;
                float gimbalPitch = float.NaN;
                try
                {
                    if (MainV2.comPort != null && MainV2.comPort.MAV != null && MainV2.comPort.MAV.cs != null)
                    {
                        gimbalYaw = MainV2.comPort.MAV.cs.campointc; // degrees
                        gimbalPitch = MainV2.comPort.MAV.cs.campointa; // degrees (pitch)
                    }
                }
                catch { }

                // read settings (defaults if missing). cone length is stored in km and converted to pixels
                double coneLenKm = 0.100; // km default
                double coneAngle = 30.0; // degrees default (full width)
                try
                {
                    var st = MissionPlanner.Utilities.Settings.Instance;
                    if (st["Companion_gimbal_cone_len"] != null)
                        double.TryParse(st["Companion_gimbal_cone_len"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out coneLenKm);
                    if (st["Companion_gimbal_cone_angle"] != null)
                        double.TryParse(st["Companion_gimbal_cone_angle"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out coneAngle);
                }
                catch { }

                if (!float.IsNaN(gimbalYaw))
                {
                    // compute world yaw: gimbalYaw is expressed relative to aircraft nose (campointc),
                    // so add plane heading to get world direction, then compute the relative rotation
                    // from the drawn plane icon (which has already been rotated by `heading`).
                    float worldYaw = heading + gimbalYaw;
                    // normalize to (-180,180]
                    while (worldYaw <= -180f) worldYaw += 360f;
                    while (worldYaw > 180f) worldYaw -= 360f;
                    float rel = worldYaw - heading;
                    // apply extra rotation so cone points to world gimbal direction with configurable offset
                    float offset = 180f;
                    try
                    {
                        var st = MissionPlanner.Utilities.Settings.Instance;
                        if (st["Companion_gimbal_cone_offset"] != null)
                        {
                            double tmp; if (double.TryParse(st["Companion_gimbal_cone_offset"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tmp)) offset = (float)tmp;
                        }
                    }
                    catch { }
                    try { g.RotateTransform(rel + offset); } catch { }

                    // convert cone length (km) -> pixels at current zoom and latitude
                    float coneLenPx = 100f;
                    try
                    {
                        double lenMeters = coneLenKm * 1000.0;
                        double lat = this.Position.Lat;
                        double zoom = Overlay.Control.Zoom;
                        // meters per pixel formula for WebMercator
                        double metersPerPixel = 156543.03392804062 * Math.Cos(lat * Math.PI / 180.0) / Math.Pow(2.0, zoom);
                        coneLenPx = (float)(lenMeters / metersPerPixel);
                    }
                    catch { coneLenPx = 100f; }

                    // adjust cone length slightly by pitch (tilt up/down)
                    float lenAdj = coneLenPx;
                    if (!float.IsNaN(gimbalPitch)) lenAdj = coneLenPx * (1f + (gimbalPitch / 90f) * 0.35f);

                    // build sector (pie section) in local marker coordinates (origin at plane center)
                    double halfAng = (coneAngle / 2.0) * Math.PI / 180.0;
                    int segments = Math.Max(6, (int)(Math.Abs(halfAng) * 180.0 / Math.PI));
                    segments = Math.Min(80, segments * 4);
                    var arcPoints = new List<PointF>();
                    for (int i = 0; i <= segments; i++)
                    {
                        double a = -halfAng + (2.0 * halfAng) * ((double)i / (double)segments);
                        float x = (float)(Math.Sin(a) * lenAdj);
                        float y = (float)(Math.Cos(a) * lenAdj);
                        arcPoints.Add(new PointF(x, y));
                    }

                    // create filled sector path (semi-transparent inside)
                    using (var path = new GraphicsPath())
                    {
                        try
                        {
                            path.AddLine(0f, 0f, arcPoints[0].X, arcPoints[0].Y);
                            path.AddLines(arcPoints.ToArray());
                            path.CloseFigure();

                            // make interior fully transparent
                            using (var fillBrush = new SolidBrush(Color.FromArgb(0, Color.Orange)))
                            {
                                g.FillPath(fillBrush, path);
                            }

                            // draw perimeter stroke
                            using (var outlinePen = new Pen(Color.FromArgb(200, Color.Orange), 2f))
                            {
                                outlinePen.LineJoin = LineJoin.Round;
                                g.DrawPath(outlinePen, path);
                            }

                            // dashed center line from origin to arc middle
                            try
                            {
                                using (var dashPen = new Pen(Color.FromArgb(200, Color.Orange), 1.5f))
                                {
                                    // longer dashes and larger gaps
                                    dashPen.DashPattern = new float[] { 12f, 8f };
                                    dashPen.DashCap = DashCap.Round;
                                    PointF mid = arcPoints[arcPoints.Count / 2];
                                    g.DrawLine(dashPen, 0f, 0f, mid.X, mid.Y);
                                }
                            }
                            catch { }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                g.Transform = temp;
            }
        }

        public override void Dispose()
        {
            try { if (icon != null) { icon.Dispose(); icon = null; } }
            catch (Exception ex) { Trace.WriteLine("GMapMarkerPlaneCustom.Dispose failed: " + ex.Message + "\n" + ex.StackTrace); }
            base.Dispose();
        }
    }
}


