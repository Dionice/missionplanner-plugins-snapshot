using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;
using MissionPlanner;
using MissionPlanner.GCSViews;
using MissionPlanner.Utilities;

namespace GimbalRS485
{
    // ─────────────────────────────────────────────────────────────────────────
    // Protocol helpers — exact port of gimbal_ctrl_emulator.py
    // ─────────────────────────────────────────────────────────────────────────
    internal static class GimbalProtocol
    {
        /// <summary>CRC-16/CCITT — poly 0x1021, init 0xFFFF (matches Python crc16()).</summary>
        public static ushort Crc16(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                    crc = ((crc & 0x8000) != 0) ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        public static byte[] ShortPacket(byte cmd, byte d1, byte d2)
            => new byte[] { 0x55, cmd, 0x00, d1, d2 };

        public static byte[] LongPacket(byte cmd, byte[] payload)
        {
            var hdr = new byte[3 + payload.Length];
            hdr[0] = 0x55; hdr[1] = cmd; hdr[2] = (byte)payload.Length;
            Array.Copy(payload, 0, hdr, 3, payload.Length);
            ushort crc = Crc16(hdr);
            var pkt = new byte[hdr.Length + 2];
            Array.Copy(hdr, pkt, hdr.Length);
            pkt[hdr.Length]     = (byte)(crc & 0xFF);
            pkt[hdr.Length + 1] = (byte)((crc >> 8) & 0xFF);
            return pkt;
        }

        // Pre-built packets
        public static readonly byte[] EXT_STATUS_REQ = ShortPacket(0x45, 0x9B, 0x8B);
        public static readonly byte[] STOP_CMD       = LongPacket(0x24, new byte[] { 0x00, 0x64 });
        public static readonly byte[] ZERO_CMD       = new byte[] { 0x55, 0x17, 0x00, 0x46, 0xE3 };
        public static readonly byte[] CALIBRATE_CMD  = new byte[] { 0x55, 0x14, 0x01, 0xA5, 0x63, 0x04 };

        /// <summary>Build a GOTO_ABS (0x1C) packet. axis 1 = PITCH, axis 0 = YAW.</summary>
        public static byte[] GotoAbsPacket(byte axis, byte speed, ushort targetRaw)
        {
            var payload = new byte[4];
            payload[0] = axis;
            payload[1] = speed;
            payload[2] = (byte)(targetRaw & 0xFF);
            payload[3] = (byte)((targetRaw >> 8) & 0xFF);
            return LongPacket(0x1C, payload);
        }

        /// <summary>SET_DEFAULT_AZ (0x18) packet.</summary>
        public static byte[] SetDefaultAzPacket(ushort az)
        {
            var payload = new byte[] { (byte)(az & 0xFF), (byte)((az >> 8) & 0xFF) };
            return LongPacket(0x18, payload);
        }

        // ── Encoding helpers ──────────────────────────────────────────────
        /// <summary>
        /// Signed degrees (−179..180) → raw unsigned 0..359 used by the gimbal.
        /// Negative angles wrap around: −10 → 350.
        /// </summary>
        public static ushort PitchToRaw(double deg)
        {
            // clamp to sensible range
            double d = ((deg % 360) + 360) % 360; // normalise 0..360
            return (ushort)Math.Round(d);
        }

        /// <summary>
        /// Raw unsigned 0..359 → signed pitch (values > 180 become negative).
        /// Matches the wrap-around fix in gimbal_ctrl_emulator.py.
        /// </summary>
        public static int RawToPitch(ushort raw) => raw > 180 ? raw - 360 : raw;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GimbalSerial — persistent serial connection + background reader
    // ─────────────────────────────────────────────────────────────────────────
    internal class GimbalSerial : IDisposable
    {
        private SerialPort _port;
        private Thread _readerThread;
        private volatile bool _running;
        private readonly object _writeLock = new object();
        private readonly List<byte> _buf = new List<byte>(512);

        public volatile int PitchRaw;   // raw unsigned (0-359, > 180 means negative)
        public volatile int YawRaw;
        public volatile int AuxRaw;

        // 0x1B movement-report frame — updated while gimbal is slewing
        public volatile int  Frame1bAz     = -1;   // -1 = never received
        public volatile int  Frame1bPitch  = 0;
        private long _lastFrame1bTicks = 0;
        /// <summary>Seconds since the last 0x1B position-report frame was received.</summary>
        public double SecondsSinceLastFrame1b =>
            _lastFrame1bTicks == 0
                ? double.MaxValue
                : (double)(Stopwatch.GetTimestamp() - _lastFrame1bTicks) / Stopwatch.Frequency;

        /// <summary>
        /// Reset the 0x1B silence timer to "just now" so completion detection starts
        /// fresh after sending a new axis command.
        /// </summary>
        public void ResetFrame1bTimer() { _lastFrame1bTicks = Stopwatch.GetTimestamp(); }

        public int Pitch => GimbalProtocol.RawToPitch((ushort)PitchRaw);
        public int Yaw   => YawRaw;

        public bool IsConnected => _port != null && _port.IsOpen;

        /// <summary>Raised (from reader thread) whenever a new EXT_STATUS frame arrives.</summary>
        public event Action PositionUpdated;

        public void Connect(string portName, int baud)
        {
            Disconnect();
            _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout  = 100,
                WriteTimeout = 500,
            };
            _port.Open();
            _running = true;
            _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "GimbalReader" };
            _readerThread.Start();
        }

        public void Disconnect()
        {
            _running = false;
            try { _port?.Close(); } catch { }
            try { _readerThread?.Join(300); } catch { }
            _port = null;
            _readerThread = null;
            _buf.Clear();
        }

        /// <summary>When true, all outgoing packets are silently dropped.</summary>
        public volatile bool RxOnly;

        public bool SendPacket(byte[] pkt)
        {
            if (!IsConnected) return false;
            if (RxOnly) return false;
            lock (_writeLock)
            {
                try { _port.Write(pkt, 0, pkt.Length); return true; }
                catch { return false; }
            }
        }

        private void ReaderLoop()
        {
            const double GAP_SEC = 0.012;
            double lastByte = 0;
            while (_running)
            {
                try
                {
                    int avail = _port.BytesToRead;
                    double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                    if (avail > 0)
                    {
                        if (_buf.Count > 0 && (now - lastByte) > GAP_SEC)
                            FlushBuffer();
                        var chunk = new byte[avail];
                        _port.Read(chunk, 0, avail);
                        foreach (var b in chunk) _buf.Add(b);
                        lastByte = now;
                    }
                    else
                    {
                        if (_buf.Count > 0 && (now - lastByte) > GAP_SEC)
                            FlushBuffer();
                        Thread.Sleep(5);
                    }
                }
                catch (TimeoutException) { }
                catch (Exception ex) when (_running)
                {
                    Trace.WriteLine("GimbalSerial reader: " + ex.Message);
                    Thread.Sleep(10);
                }
            }
        }

        private void FlushBuffer()
        {
            var raw = _buf.ToArray();
            _buf.Clear();
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == 0x55 && i + 2 < raw.Length)
                {
                    byte third = raw[i + 2];
                    if (third > 0x02) // long packet
                    {
                        int total = 3 + third + 2;
                        if (i + total <= raw.Length)
                        {
                            ParseFrame(raw, i, total);
                            i += total;
                            continue;
                        }
                    }
                    else // short packet
                    {
                        if (i + 5 <= raw.Length)
                        {
                            ParseFrame(raw, i, 5);
                            i += 5;
                            continue;
                        }
                    }
                }
                i++;
            }
        }

        private void ParseFrame(byte[] raw, int start, int len)
        {
            if (len < 3) return;
            byte cmd = raw[start + 1];
            if (cmd == 0x45 && len >= 3 + 6) // EXT_STATUS with >=6 payload bytes
            {
                int pStart = start + 3;
                ushort ax1 = (ushort)(raw[pStart]     | (raw[pStart + 1] << 8));
                ushort ax2 = (ushort)(raw[pStart + 2] | (raw[pStart + 3] << 8));
                ushort ax3 = (ushort)(raw[pStart + 4] | (raw[pStart + 5] << 8));
                // Mirror python log_rx: axis1=pitch, axis2=yaw, axis3=aux (roll)
                PitchRaw = ax1;
                YawRaw   = ax3;
                AuxRaw   = ax2;
                try { PositionUpdated?.Invoke(); } catch { }
            }
            // 0x1B — live position report sent while gimbal is slewing
            // Format: 55 1B <len=4> <pitch_lo> <pitch_hi> <az_lo> <az_hi> <crc_lo> <crc_hi>
            if (cmd == 0x1B && len >= 7)
            {
                int pStart = start + 3;
                short  pitch1b = (short)(raw[pStart]     | (raw[pStart + 1] << 8));
                ushort az1b    = (ushort)(raw[pStart + 2] | (raw[pStart + 3] << 8));
                Frame1bPitch = pitch1b;
                Frame1bAz    = az1b;
                _lastFrame1bTicks = Stopwatch.GetTimestamp();
            }
        }

        public void Dispose() => Disconnect();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Plugin
    // ─────────────────────────────────────────────────────────────────────────
    public class GimbalRS485Plugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name    { get { return "GimbalRS485"; } }
        public override string Version { get { return "1.0"; } }
        public override string Author  { get { return "Dionice"; } }

        // ── Serial ─────────────────────────────────────────────────────────
        private readonly GimbalSerial _gimbal = new GimbalSerial();
        private readonly GimbalSerial _gimbal2 = new GimbalSerial();

        // ── Gimbal ground position ─────────────────────────────────────────
        private double _gimbalLat = double.NaN;
        private double _gimbalLng = double.NaN;
        private double _gimbalAltM = 0.0;   // MSL metres
        private double _gimbalLat2 = double.NaN;
        private double _gimbalLng2 = double.NaN;
        private double _gimbalAltM2 = 0.0;  // MSL metres for gimbal 2

        // ── Map ────────────────────────────────────────────────────────────
        private GMapOverlay   _overlay;
        private GMapMarker    _gimbalMarker;
        private GMapRoute     _directionLine;
        private GMapMarker    _gimbalMarker2;
        private GMapRoute     _directionLine2;
        private const int     ICON_SIZE = 56;

        // ── Drag state ─────────────────────────────────────────────────────
        private bool _isDraggingGimbal  = false;
        private bool _prevCanDragMap    = true;
        private bool _markerMouseOver   = false;
        private bool _markerHandlersAttached = false;
        private IMessageFilter _globalMapFilter = null;

        // ── RX-only mode ──────────────────────────────────────────────────
        private bool _rxOnly = false;

        // ── Yaw offset ────────────────────────────────────────────────────
        private int _yawOffset = 0;
        /// <summary>Get adjusted yaw for a gimbal index, applying per-gimbal yaw offset (deg) and normalising 0–359.</summary>
        private double GetAdjustedYaw(int idx)
        {
            var serial = idx == 0 ? _gimbal : _gimbal2;
            var ui = (_gimbals != null && idx >= 0 && idx < _gimbals.Length) ? _gimbals[idx] : null;
            int off = ui?.YawOffset ?? _yawOffset;
            double yawVal = serial?.Yaw ?? 0.0;
            return (yawVal + off + 3600.0) % 360.0;
        }

        // ── Auto-aim ───────────────────────────────────────────────────────
        private const double AUTO_AIM_DEADBAND_DEG = 1.0;

        // Sequential movement state machine (now per-gimbal via GimbalUI)
        private enum AimState { Idle, WaitingYaw, WaitingPitch }

        // ── Track source ─────────────────────────────────────────────────
        // Per-gimbal `TrackCompanion` moved into `GimbalUI`.

        // ── UI controls (kept for cross-method access) ────────────────────
        private TabPage _tabPage;
        private TabControl _gimbalTabControl;
        private TabPage _gimbalTab1;
        private TabPage _gimbalTab2;
        // Per-gimbal state and controls
        private GimbalUI[] _gimbals = new GimbalUI[2];
        private System.Windows.Forms.Timer _uiTimer;
        // Legacy/single-gimbal UI fields (kept for compatibility)
        private Label _lblConnStatus;
        private Label _lblPitch;
        private Label _lblYaw;
        private Label _lblAux;

        private class GimbalUI
        {
            public GimbalSerial Serial;
            public double Lat = double.NaN, Lng = double.NaN, AltM = 0.0;
            public int YawOffset = 0;
            public int PitchOffset = 0;
            public bool RxOnly = false;
            public bool AutoAim = false;
            public double LastSentYaw = double.NaN, LastSentPitch = double.NaN;
            public double TargetYaw = double.NaN, TargetPitch = double.NaN;
            public ushort PendingPitchRaw;
            public double PendingPitchDeg;
            public byte PendingSpeed;
            public bool PendingPitchNeeded;
            public int AimTickCount = 0;
            public long AimStateEnteredTicks = 0;
            public AimState AimState = AimState.Idle;
            public bool Show1bAzLine = false;
            public GMapRoute Az1bLine = null;
            public TabPage Tab;
            public Label LblPitch, LblYaw, LblAux, LblConnStatus, LblPointStatus, LblGimbalPos, LblGotoStatus;
            public Button BtnAutoAim;
            public TextBox TxtAutoAimSpeed;
            public bool TrackCompanion = false;
            // Add more per-gimbal controls as needed
        }

        // ── Right-click menu items ─────────────────────────────────────────
        private ToolStripMenuItem _menuSetGimbal1;
        private ToolStripMenuItem _menuSetGimbal2;

        // Show 0x1B azimuth line (global toggle)
        private bool _show1bAzLine = false;

        // ── Settings keys ─────────────────────────────────────────────────
        private const string KEY_LAT  = "GimbalRS485_lat";
        private const string KEY_LNG  = "GimbalRS485_lng";
        private const string KEY_ALT  = "GimbalRS485_alt";
        private const string KEY_LAT2 = "GimbalRS485_lat_2";
        private const string KEY_LNG2 = "GimbalRS485_lng_2";
        private const string KEY_ALT2 = "GimbalRS485_alt_2";
        private const string KEY_PORT = "GimbalRS485_port";
        private const string KEY_BAUD = "GimbalRS485_baud";
        private const string KEY_PORT2 = "GimbalRS485_port_2";
        private const string KEY_BAUD2 = "GimbalRS485_baud_2";
        private const string KEY_PITCH_OFFSET = "GimbalRS485_pitch_offset";
        private const string KEY_PITCH_OFFSET2 = "GimbalRS485_pitch_offset_2";

        // ─────────────────────────────────────────────────────────────────
        public override bool Init() { return true; }

        public override bool Loaded()
        {
            try { LoadSettings(); }    catch { }
            try { SetupOverlay(); }    catch { }
            try { BuildTab(); }        catch { }
            try { AddMapMenuItem(); }  catch { }
            try { StartUiTimer(); }    catch { }
            _gimbal.PositionUpdated += OnPositionUpdated;
            try { _gimbal2.PositionUpdated += OnPositionUpdated; } catch { }

            // Attach marker enter/leave to suppress map panning when hovering
            try
            {
                if (FlightData.instance?.gMapControl1 != null && !_markerHandlersAttached)
                {
                    FlightData.instance.gMapControl1.OnMarkerEnter += GMap_OnMarkerEnter;
                    FlightData.instance.gMapControl1.OnMarkerLeave += GMap_OnMarkerLeave;
                    _markerHandlersAttached = true;
                }
            }
            catch { }

            // Install global message filter for drag support
            try
            {
                if (_globalMapFilter == null)
                {
                    _globalMapFilter = new GlobalMapMouseFilter(this);
                    Application.AddMessageFilter(_globalMapFilter);
                }
            }
            catch { }

            return true;
        }

        public override bool Exit()
        {
            try { _gimbal.PositionUpdated -= OnPositionUpdated; } catch { }
            try { _uiTimer?.Stop(); _uiTimer?.Dispose(); } catch { }
            try { _gimbal.Disconnect(); } catch { }
            try { RemoveTab(); } catch { }
            try { RemoveMapMenuItem(); } catch { }
            try { TeardownOverlay(); } catch { }
            try
            {
                if (_globalMapFilter != null) { Application.RemoveMessageFilter(_globalMapFilter); _globalMapFilter = null; }
            }
            catch { }
            try
            {
                if (_markerHandlersAttached && FlightData.instance?.gMapControl1 != null)
                {
                    FlightData.instance.gMapControl1.OnMarkerEnter -= GMap_OnMarkerEnter;
                    FlightData.instance.gMapControl1.OnMarkerLeave -= GMap_OnMarkerLeave;
                    _markerHandlersAttached = false;
                }
            }
            catch { }
            return true;
        }

        // ─── Settings ──────────────────────────────────────────────────────
        private void LoadSettings()
        {
            var s = Settings.Instance;
            TryGetDouble(s, KEY_LAT,  ref _gimbalLat);
            TryGetDouble(s, KEY_LNG,  ref _gimbalLng);
            TryGetDouble(s, KEY_ALT,  ref _gimbalAltM);
            TryGetDouble(s, KEY_LAT2, ref _gimbalLat2);
            TryGetDouble(s, KEY_LNG2, ref _gimbalLng2);
            TryGetDouble(s, KEY_ALT2, ref _gimbalAltM2);
        }

        private void SaveSettings()
        {
            var s = Settings.Instance;
            if (!double.IsNaN(_gimbalLat)) s[KEY_LAT] = _gimbalLat.ToString(CultureInfo.InvariantCulture);
            if (!double.IsNaN(_gimbalLng)) s[KEY_LNG] = _gimbalLng.ToString(CultureInfo.InvariantCulture);
            s[KEY_ALT] = _gimbalAltM.ToString(CultureInfo.InvariantCulture);
            if (!double.IsNaN(_gimbalLat2)) s[KEY_LAT2] = _gimbalLat2.ToString(CultureInfo.InvariantCulture);
            if (!double.IsNaN(_gimbalLng2)) s[KEY_LNG2] = _gimbalLng2.ToString(CultureInfo.InvariantCulture);
            s[KEY_ALT2] = _gimbalAltM2.ToString(CultureInfo.InvariantCulture);
            try
            {
                if (_gimbals != null && _gimbals.Length > 0 && _gimbals[0] != null)
                    s[KEY_PITCH_OFFSET] = _gimbals[0].PitchOffset.ToString(CultureInfo.InvariantCulture);
                if (_gimbals != null && _gimbals.Length > 1 && _gimbals[1] != null)
                    s[KEY_PITCH_OFFSET2] = _gimbals[1].PitchOffset.ToString(CultureInfo.InvariantCulture);
            }
            catch { }
            try { s.Save(); } catch { }
        }

        private static bool TryGetDouble(Settings s, string key, ref double val)
        {
            try
            {
                var v = s[key];
                if (v == null) return false;
                return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out val);
            }
            catch { return false; }
        }

        // ─── Map overlay ───────────────────────────────────────────────────
        private void SetupOverlay()
        {
            _overlay = new GMapOverlay("gimbal_rs485");
            FlightData.instance.gMapControl1.Overlays.Add(_overlay);
            RefreshMapObjects();
        }

        private void TeardownOverlay()
        {
            try
            {
                _overlay?.Markers.Clear();
                _overlay?.Routes.Clear();
                FlightData.instance?.gMapControl1?.Overlays.Remove(_overlay);
            }
            catch { }
        }

        private void RefreshMapObjects()
        {
            if (_overlay == null) return;
            _overlay.Markers.Clear();
            _overlay.Routes.Clear();

            if (!double.IsNaN(_gimbalLat) && !double.IsNaN(_gimbalLng))
            {
                var pos = new PointLatLng(_gimbalLat, _gimbalLng);
                var bmp = GetAntennaTrackerIcon(ICON_SIZE);
                _gimbalMarker = new GMarkerGoogle(pos, bmp)
                {
                    ToolTipText  = "Gimbal 1",
                    ToolTipMode  = MarkerTooltipMode.OnMouseOver,
                };
                _gimbalMarker.Offset = new Point(-ICON_SIZE / 2, -ICON_SIZE / 2);
                _overlay.Markers.Add(_gimbalMarker);

                var endPt = ProjectPoint(pos, GetAdjustedYaw(0), 3000);
                _directionLine = new GMapRoute(new List<PointLatLng> { pos, endPt }, "dir1")
                {
                    Stroke = new System.Drawing.Pen(System.Drawing.Color.OrangeRed, 5) { DashStyle = System.Drawing.Drawing2D.DashStyle.Solid },
                    IsVisible = true,
                };
                _overlay.Routes.Add(_directionLine);
            }
            else
            {
                _gimbalMarker  = null;
                _directionLine = null;
            }

            // Secondary gimbal marker / direction
            if (!double.IsNaN(_gimbalLat2) && !double.IsNaN(_gimbalLng2))
            {
                var pos2 = new PointLatLng(_gimbalLat2, _gimbalLng2);
                var bmp2 = GetAntennaTrackerIcon(ICON_SIZE);
                _gimbalMarker2 = new GMarkerGoogle(pos2, bmp2)
                {
                    ToolTipText = "Gimbal 2",
                    ToolTipMode = MarkerTooltipMode.OnMouseOver,
                };
                _gimbalMarker2.Offset = new Point(-ICON_SIZE / 2, -ICON_SIZE / 2);
                _overlay.Markers.Add(_gimbalMarker2);

                double adjYaw2 = (_gimbal2.Yaw + _yawOffset + 3600.0) % 360.0;
                var endPt2 = ProjectPoint(pos2, adjYaw2, 3000);
                _directionLine2 = new GMapRoute(new List<PointLatLng> { pos2, endPt2 }, "dir2")
                {
                    Stroke = new System.Drawing.Pen(System.Drawing.Color.DarkOrange, 4) { DashStyle = System.Drawing.Drawing2D.DashStyle.Solid },
                    IsVisible = true,
                };
                _overlay.Routes.Add(_directionLine2);
            }
            else
            {
                _gimbalMarker2  = null;
                _directionLine2 = null;
            }

            try { FlightData.instance?.gMapControl1?.Refresh(); } catch { }
        }

        private void UpdateDirectionLine()
        {
            if (_overlay == null) return;

            // Helper to update one gimbal's direction and 1B az line
            void updateFor(int idx, GMapMarker marker, GMapRoute dir, GimbalSerial serial)
            {
                if (marker == null || dir == null) return;
                var pos = marker.Position;
                double yaw = 0;
                yaw = GetAdjustedYaw(idx);
                var endPt = ProjectPoint(pos, yaw, 3000);
                dir.Points.Clear();
                dir.Points.Add(pos);
                dir.Points.Add(endPt);
                try { dir.Overlay?.Control?.UpdateRouteLocalPosition(dir); } catch { }

                // 0x1B azimuth: per-gimbal UI route
                if (_gimbals != null && idx < _gimbals.Length)
                {
                    var ui = _gimbals[idx];
                    bool want = _show1bAzLine || (ui?.Show1bAzLine ?? false);
                    if (!want)
                    {
                        if (ui?.Az1bLine != null) { try { _overlay.Routes.Remove(ui.Az1bLine); } catch { } ui.Az1bLine = null; }
                    }
                    else
                    {
                        try { if (ui?.Az1bLine != null) { _overlay.Routes.Remove(ui.Az1bLine); ui.Az1bLine = null; } } catch { }
                        int az = serial?.Frame1bAz ?? -1;
                        if (az >= 0)
                        {
                            double lenMeters = 3000.0;
                            bool havePlane = false;
                            double planeLat = double.NaN, planeLng = double.NaN, planeAlt = 0.0;
                            try
                            {
                                if (ui?.TrackCompanion ?? false)
                                {
                                    if (TryGetCompanionPlaneData(out planeLat, out planeLng, out planeAlt))
                                        havePlane = true;
                                    else if (MainV2.comPort?.MAV?.cs != null)
                                    {
                                        planeLat = MainV2.comPort.MAV.cs.lat;
                                        planeLng = MainV2.comPort.MAV.cs.lng;
                                        planeAlt = MainV2.comPort.MAV.cs.alt / CurrentState.multiplieralt;
                                        if (!(planeLat == 0.0 && planeLng == 0.0)) havePlane = true;
                                    }
                                }
                                else
                                {
                                    if (MainV2.comPort?.MAV?.cs != null)
                                    {
                                        planeLat = MainV2.comPort.MAV.cs.lat;
                                        planeLng = MainV2.comPort.MAV.cs.lng;
                                        planeAlt = MainV2.comPort.MAV.cs.alt / CurrentState.multiplieralt;
                                        if (!(planeLat == 0.0 && planeLng == 0.0)) havePlane = true;
                                    }
                                }
                            }
                            catch { }

                            if (havePlane)
                            {
                                try { lenMeters = Math.Max(5.0, HaversineMeters(pos.Lat, pos.Lng, planeLat, planeLng)); } catch { lenMeters = 3000.0; }
                            }

                            var end1b = ProjectPoint(pos, az, lenMeters);
                            var route = new GMapRoute(new List<PointLatLng> { pos, end1b }, $"az1b_{idx}")
                            {
                                Stroke = new System.Drawing.Pen(System.Drawing.Color.DodgerBlue, 3) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash },
                                IsVisible = true,
                            };
                            ui.Az1bLine = route;
                            _overlay.Routes.Add(route);
                            try { route.Overlay?.Control?.UpdateRouteLocalPosition(route); } catch { }
                        }
                    }
                }
            }

            updateFor(0, _gimbalMarker, _directionLine, _gimbal);
            updateFor(1, _gimbalMarker2, _directionLine2, _gimbal2);

            try { FlightData.instance?.gMapControl1?.Refresh(); } catch { }
        }

        // ─── Tab ───────────────────────────────────────────────────────────
        private void BuildTab()
        {
            Action createTab = () =>
            {
                try
                {
                    _tabPage = new TabPage
                    {
                        AutoScroll = true,
                        Text       = "GIMBAL RS485",
                        Name       = "tabGimbalRS485",
                    };

                    int y = 6;
                    const int LW = 110; // label width
                    const int EW = 90;  // entry width
                    const int BW_SM = 80;
                    const int ROW = 28;

                    // ── Section: Connection ─────────────────────────────────
                    AddSectionLabel(_tabPage, "Connection", ref y);

                    var lblPort = NewLabel("COM Port:", 6, y, LW);
                    var cmbPort = new ComboBox { Left = 6 + LW, Top = y, Width = EW };
                    cmbPort.Items.AddRange(SerialPort.GetPortNames());
                    var savedPort = Settings.Instance[KEY_PORT]?.ToString() ?? "";
                    if (cmbPort.Items.Contains(savedPort)) cmbPort.SelectedItem = savedPort;
                    else if (cmbPort.Items.Count > 0) cmbPort.SelectedIndex = 0;
                    _tabPage.Controls.Add(lblPort);
                    _tabPage.Controls.Add(cmbPort);
                    y += ROW;

                    var lblBaud = NewLabel("Baud Rate:", 6, y, LW);
                    var cmbBaud = new ComboBox { Left = 6 + LW, Top = y, Width = EW };
                    cmbBaud.Items.AddRange(new object[] { "9600", "57600", "115200", "230400" });
                    var savedBaud = Settings.Instance[KEY_BAUD]?.ToString() ?? "115200";
                    cmbBaud.SelectedItem = cmbBaud.Items.Contains(savedBaud) ? (object)savedBaud : "115200";
                    _tabPage.Controls.Add(lblBaud);
                    _tabPage.Controls.Add(cmbBaud);
                    y += ROW;

                    var btnConnect    = new Button { Left = 6,       Top = y, Width = BW_SM, Text = "Connect" };
                    var btnDisconnect = new Button { Left = 6 + BW_SM + 4, Top = y, Width = BW_SM, Text = "Disconnect" };
                    _lblConnStatus    = NewLabel("Not connected", 6 + 2 * (BW_SM + 4), y, 160);
                    _lblConnStatus.ForeColor = Color.Gray;

                    btnConnect.Click += (s, e) =>
                    {
                        try
                        {
                            string port = cmbPort.SelectedItem?.ToString();
                            if (string.IsNullOrEmpty(port)) { _lblConnStatus.Text = "Select a port"; return; }
                            int baud = int.Parse(cmbBaud.SelectedItem?.ToString() ?? "115200");
                            _gimbal.Connect(port, baud);
                            _lblConnStatus.Text      = "● Connected";
                            _lblConnStatus.ForeColor = Color.Green;
                            Settings.Instance[KEY_PORT] = port;
                            Settings.Instance[KEY_BAUD] = baud.ToString();
                            try { Settings.Instance.Save(); } catch { }
                        }
                        catch (Exception ex)
                        {
                            _lblConnStatus.Text      = "Error: " + ex.Message;
                            _lblConnStatus.ForeColor = Color.Red;
                        }
                    };

                    btnDisconnect.Click += (s, e) =>
                    {
                        _gimbal.Disconnect();
                        _lblConnStatus.Text      = "Disconnected";
                        _lblConnStatus.ForeColor = Color.Gray;
                    };

                    _tabPage.Controls.Add(btnConnect);
                    _tabPage.Controls.Add(btnDisconnect);
                    _tabPage.Controls.Add(_lblConnStatus);
                    y += ROW;

                    var chkRxOnly = new CheckBox
                    {
                        Left     = 6,
                        Top      = y,
                        Width    = 200,
                        Text     = "RX only (no TX to gimbal)",
                        Checked  = _rxOnly,
                    };
                    chkRxOnly.CheckedChanged += (s, e) => { _rxOnly = chkRxOnly.Checked; _gimbal.RxOnly = _rxOnly; };
                    _tabPage.Controls.Add(chkRxOnly);
                    y += ROW + 4;

                    // ── Section: Live Position ──────────────────────────────
                    AddSectionLabel(_tabPage, "Live Position", ref y);

                    var lblPitchL = NewLabel("Pitch (ax1 0x45):", 6, y, LW + 10);
                    _lblPitch = new Label { Left = 6 + LW + 10, Top = y, Width = 60, Text = "—",
                        Font = new Font("Arial", 13, FontStyle.Bold), ForeColor = Color.FromArgb(0, 85, 204) };
                    var lblYawL = NewLabel("Yaw (ax3 0x45):", 6 + LW + 80, y, LW + 10);
                    _lblYaw = new Label { Left = 6 + 2*(LW + 80), Top = y, Width = 60, Text = "—",
                        Font = new Font("Arial", 13, FontStyle.Bold), ForeColor = Color.FromArgb(204, 85, 0) };
                    var lblAuxL = NewLabel("Aux:", 6 + 3*(LW + 30), y, 40);
                    _lblAux = NewLabel("—", 6 + 3*(LW + 30) + 44, y, 50);
                    _tabPage.Controls.Add(lblPitchL);
                    _tabPage.Controls.Add(_lblPitch);
                    _tabPage.Controls.Add(lblYawL);
                    _tabPage.Controls.Add(_lblYaw);
                    _tabPage.Controls.Add(lblAuxL);
                    _tabPage.Controls.Add(_lblAux);
                    y += ROW;

                    var lblYawOff = NewLabel("Yaw Offset (°):", 6, y, 110);
                    var txtYawOff = new TextBox { Left = 6 + 114, Top = y, Width = 55,
                        Text = _yawOffset.ToString() };
                    var lblYawOffHint = NewLabel("added to raw yaw for display & map direction", 6 + 114 + 60, y, 260);
                    lblYawOffHint.ForeColor = Color.Gray;
                    txtYawOff.TextChanged += (s, e) =>
                    {
                        if (int.TryParse(txtYawOff.Text.Trim(), out int ofs))
                            _yawOffset = ofs;
                    };
                    _tabPage.Controls.Add(lblYawOff);
                    _tabPage.Controls.Add(txtYawOff);
                    _tabPage.Controls.Add(lblYawOffHint);
                    y += ROW + 4;

                    // Global pitch offset control (for legacy single-tab UI)
                    var lblPitchOffG = NewLabel("Pitch Offset (°):", 6, y, 110);
                    var savedPitchG = Settings.Instance[KEY_PITCH_OFFSET]?.ToString() ?? "0";
                    var txtPitchOffG = new TextBox { Left = 6 + 114, Top = y, Width = 55, Text = savedPitchG };
                    var btnCalPitchG = new Button { Left = 6 + 114 + 62, Top = y - 4, Width = 80, Text = "Cal Pitch" };
                    txtPitchOffG.TextChanged += (s, e) => { if (int.TryParse(txtPitchOffG.Text.Trim(), out int pofs)) { Settings.Instance[KEY_PITCH_OFFSET] = pofs.ToString(CultureInfo.InvariantCulture); } };
                    btnCalPitchG.Click += (s, e) => { try { int cur = _gimbal?.Pitch ?? 0; txtPitchOffG.Text = cur.ToString(); Settings.Instance[KEY_PITCH_OFFSET] = cur.ToString(CultureInfo.InvariantCulture); } catch { } };
                    _tabPage.Controls.Add(lblPitchOffG); _tabPage.Controls.Add(txtPitchOffG); _tabPage.Controls.Add(btnCalPitchG);
                    y += ROW + 4;

                    // ── Section: Native GOTO ────────────────────────────────
                    AddSectionLabel(_tabPage, "Native GOTO (0x1C)", ref y);

                    var lblTP = NewLabel("Target Pitch:", 6, y, LW);
                    var txtPitch = new TextBox { Left = 6 + LW, Top = y, Width = 60 };
                    var lblTY = NewLabel("Target Yaw:", 6 + LW + 70, y, LW);
                    var txtYaw = new TextBox { Left = 6 + 2*LW + 70, Top = y, Width = 60 };
                    var lblSpd = NewLabel("Speed:", 6 + 2*LW + 140, y, 50);
                    var txtSpeed = new TextBox { Left = 6 + 2*LW + 192, Top = y, Width = 50, Text = "50" };
                    _tabPage.Controls.Add(lblTP); _tabPage.Controls.Add(txtPitch);
                    _tabPage.Controls.Add(lblTY); _tabPage.Controls.Add(txtYaw);
                    _tabPage.Controls.Add(lblSpd); _tabPage.Controls.Add(txtSpeed);
                    y += ROW;

                    var btnGoPitch  = new Button { Left = 6,                Top = y, Width = BW_SM, Text = "▶ Go Pitch" };
                    var btnGoYaw    = new Button { Left = 6 + BW_SM + 4,    Top = y, Width = BW_SM, Text = "▶ Go Yaw" };
                    var btnCancel   = new Button { Left = 6 + 2*(BW_SM+4),  Top = y, Width = BW_SM, Text = "■ Cancel" };
                    var lblGotoSt   = NewLabel("", 6 + 3*(BW_SM+4), y, 200);
                    lblGotoSt.ForeColor = Color.DimGray;

                    btnGoPitch.Click += (s, e) =>
                    {
                        try
                        {
                            int pitch = int.Parse(txtPitch.Text.Trim());
                            byte speed = (byte)Clamp(int.Parse(txtSpeed.Text.Trim()), 1, 255);
                            int off = 0;
                            if (_gimbals != null && _gimbals.Length > 0 && _gimbals[0] != null) off = _gimbals[0].PitchOffset;
                            else { int.TryParse(Settings.Instance[KEY_PITCH_OFFSET]?.ToString() ?? "0", out off); }
                            ushort raw = GimbalProtocol.PitchToRaw(pitch + off);
                            _gimbal.SendPacket(GimbalProtocol.GotoAbsPacket(1, speed, raw));
                            lblGotoSt.Text = $"Sent pitch={pitch} (raw={raw}) spd={speed} (off={off})";
                            lblGotoSt.ForeColor = Color.Green;
                        }
                        catch (Exception ex) { lblGotoSt.Text = ex.Message; lblGotoSt.ForeColor = Color.Red; }
                    };

                    btnGoYaw.Click += (s, e) =>
                    {
                        try
                        {
                            ushort raw = (ushort)Clamp(int.Parse(txtYaw.Text.Trim()), 0, 65535);
                            byte speed = (byte)Clamp(int.Parse(txtSpeed.Text.Trim()), 1, 255);
                            _gimbal.SendPacket(GimbalProtocol.GotoAbsPacket(0, speed, raw));
                            lblGotoSt.Text = $"Sent yaw={raw} spd={speed}";
                            lblGotoSt.ForeColor = Color.Green;
                        }
                        catch (Exception ex) { lblGotoSt.Text = ex.Message; lblGotoSt.ForeColor = Color.Red; }
                    };

                    btnCancel.Click += (s, e) =>
                    {
                        _gimbal.SendPacket(GimbalProtocol.STOP_CMD);
                        lblGotoSt.Text = "Stop sent";
                        lblGotoSt.ForeColor = Color.DimGray;
                    };

                    _tabPage.Controls.Add(btnGoPitch);
                    _tabPage.Controls.Add(btnGoYaw);
                    _tabPage.Controls.Add(btnCancel);
                    _tabPage.Controls.Add(lblGotoSt);
                    y += ROW + 4;

                    // ── Section: Commands ───────────────────────────────────
                    AddSectionLabel(_tabPage, "Commands", ref y);

                    var btnZero  = new Button { Left = 6,             Top = y, Width = BW_SM, Text = "Zero" };
                    var btnCalib = new Button { Left = 6 + BW_SM + 4, Top = y, Width = BW_SM, Text = "Calibrate" };

                    var lblAzLbl = NewLabel("Default Azimuth:", 6 + 2*(BW_SM+4), y, 110);
                    var txtAz    = new TextBox { Left = 6 + 2*(BW_SM+4) + 114, Top = y, Width = 60, Text = "200" };
                    var btnSetAz = new Button  { Left = 6 + 2*(BW_SM+4) + 180, Top = y, Width = 80, Text = "Set Az" };
                    var lblCmdSt = NewLabel("", 6, y + ROW, 300);
                    lblCmdSt.ForeColor = Color.DimGray;

                    btnZero.Click  += (s, e) =>
                    {
                        _gimbal.SendPacket(GimbalProtocol.ZERO_CMD);
                        lblCmdSt.Text = "Zero sent"; lblCmdSt.ForeColor = Color.Green;
                    };
                    btnCalib.Click += (s, e) =>
                    {
                        _gimbal.SendPacket(GimbalProtocol.CALIBRATE_CMD);
                        lblCmdSt.Text = "Calibrate sent"; lblCmdSt.ForeColor = Color.Green;
                    };
                    btnSetAz.Click += (s, e) =>
                    {
                        try
                        {
                            ushort az = (ushort)Clamp(int.Parse(txtAz.Text.Trim()), 0, 65535);
                            _gimbal.SendPacket(GimbalProtocol.SetDefaultAzPacket(az));
                            lblCmdSt.Text = $"Set Az={az} sent"; lblCmdSt.ForeColor = Color.Green;
                        }
                        catch (Exception ex) { lblCmdSt.Text = ex.Message; lblCmdSt.ForeColor = Color.Red; }
                    };

                    _tabPage.Controls.Add(btnZero);
                    _tabPage.Controls.Add(btnCalib);
                    _tabPage.Controls.Add(lblAzLbl);
                    _tabPage.Controls.Add(txtAz);
                    _tabPage.Controls.Add(btnSetAz);
                    _tabPage.Controls.Add(lblCmdSt);
                    y += ROW + ROW + 4;

                    // ── Section: Point at Plane ────────────────────────────
                    AddSectionLabel(_tabPage, "Point at Plane (auto-aim)", ref y);
                    // ── Gimbal device tabs ───────────────
                    _gimbalTabControl = new TabControl
                    {
                        Left = 0,
                        Top = y,
                        Width = 600,
                        Height = 700,
                    };
                    _gimbalTab1 = new TabPage { Text = "Gimbal 1" };
                    _gimbalTab2 = new TabPage { Text = "Gimbal 2" };
                    _gimbalTabControl.TabPages.Add(_gimbalTab1);
                    _gimbalTabControl.TabPages.Add(_gimbalTab2);
                    _tabPage.Controls.Add(_gimbalTabControl);
                    // Build per-gimbal controls inside each sub-tab
                    BuildGimbalControls(0, _gimbalTab1);
                    BuildGimbalControls(1, _gimbalTab2);
                    y += _gimbalTabControl.Height + 8;

                    // ── Insert tab ─────────────────────────────────────────
                    Host.MainForm.FlightData.TabListOriginal.Add(_tabPage);
                    var tabctrl = Host.MainForm.FlightData.tabControlactions;
                    if (!tabctrl.TabPages.Contains(_tabPage))
                        tabctrl.TabPages.Insert(Math.Min(5, tabctrl.TabPages.Count), _tabPage);
                    ThemeManager.ApplyThemeTo(_tabPage);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("GimbalRS485Plugin BuildTab failed: " + ex.Message);
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

        private void RemoveTab()
        {
            try
            {
                if (Host?.MainForm?.FlightData == null) return;
                var tabctrl = Host.MainForm.FlightData.tabControlactions;
                if (_tabPage != null && tabctrl.TabPages.Contains(_tabPage))
                    tabctrl.TabPages.Remove(_tabPage);
            }
            catch { }
        }

        // ─── Map right-click menu ─────────────────────────────────────────
        private void AddMapMenuItem()
        {
            _menuSetGimbal1 = new ToolStripMenuItem("Set Gimbal 1 Position Here");
            _menuSetGimbal1.Click += OnSetGimbalPosition;
            _menuSetGimbal2 = new ToolStripMenuItem("Set Gimbal 2 Position Here");
            _menuSetGimbal2.Click += OnSetGimbalPosition;
            try { Host.FDMenuMap.Items.Insert(0, _menuSetGimbal2); } catch { }
            try { Host.FDMenuMap.Items.Insert(0, _menuSetGimbal1); } catch { }
        }

        private void RemoveMapMenuItem()
        {
            try { if (_menuSetGimbal1 != null) Host.FDMenuMap.Items.Remove(_menuSetGimbal1); } catch { }
            try { if (_menuSetGimbal2 != null) Host.FDMenuMap.Items.Remove(_menuSetGimbal2); } catch { }
        }

        private void OnSetGimbalPosition(object sender, EventArgs e)
        {
            try
            {
                var p = Host.FDMenuMapPosition;
                if (p == null) return;
                if (sender == _menuSetGimbal2)
                {
                    _gimbalLat2 = p.Lat; _gimbalLng2 = p.Lng;
                    try { if (_gimbals != null && _gimbals.Length > 1 && _gimbals[1] != null) { _gimbals[1].Lat = p.Lat; _gimbals[1].Lng = p.Lng; _gimbals[1].AltM = _gimbalAltM2; } } catch { }
                }
                else
                {
                    _gimbalLat = p.Lat; _gimbalLng = p.Lng;
                    try { if (_gimbals != null && _gimbals.Length > 0 && _gimbals[0] != null) { _gimbals[0].Lat = p.Lat; _gimbals[0].Lng = p.Lng; _gimbals[0].AltM = _gimbalAltM; } } catch { }
                }
                SaveSettings();
                try { FlightData.instance?.BeginInvoke(new Action(() => {
                    try
                    {
                        if (_gimbals != null)
                        {
                            if (sender == _menuSetGimbal2)
                            {
                                if (_gimbals.Length > 1 && _gimbals[1]?.LblGimbalPos != null) _gimbals[1].LblGimbalPos.Text = GimbalPosText(1);
                            }
                            else
                            {
                                if (_gimbals.Length > 0 && _gimbals[0]?.LblGimbalPos != null) _gimbals[0].LblGimbalPos.Text = GimbalPosText(0);
                            }
                        }
                    }
                    catch { }
                })); } catch { try { if (_gimbals != null)
                {
                    if (sender == _menuSetGimbal2) { if (_gimbals.Length > 1 && _gimbals[1]?.LblGimbalPos != null) _gimbals[1].LblGimbalPos.Text = GimbalPosText(1); }
                    else { if (_gimbals.Length > 0 && _gimbals[0]?.LblGimbalPos != null) _gimbals[0].LblGimbalPos.Text = GimbalPosText(0); }
                } } catch { } }
                try { FlightData.instance?.gMapControl1?.BeginInvoke(new Action(RefreshMapObjects)); }
                catch { RefreshMapObjects(); }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("GimbalRS485Plugin: SetGimbalPosition failed: " + ex.Message);
            }
        }

        // Called by the drag filter after the marker is dropped
        internal void OnGimbalMarkerDropped(PointLatLng p, int idx)
        {
            if (idx == 1)
            {
                _gimbalLat2 = p.Lat; _gimbalLng2 = p.Lng;
            }
            else
            {
                _gimbalLat = p.Lat; _gimbalLng = p.Lng;
            }
            try { if (_gimbals != null && idx >= 0 && idx < _gimbals.Length && _gimbals[idx] != null) { _gimbals[idx].Lat = p.Lat; _gimbals[idx].Lng = p.Lng; _gimbals[idx].AltM = (idx == 0 ? _gimbalAltM : _gimbalAltM2); } } catch { }
            SaveSettings();
            try { FlightData.instance?.BeginInvoke(new Action(() => {
                try
                {
                    if (_gimbals != null)
                    {
                        if (idx == 1)
                        {
                            if (_gimbals.Length > 1 && _gimbals[1]?.LblGimbalPos != null) _gimbals[1].LblGimbalPos.Text = GimbalPosText(1);
                        }
                        else
                        {
                            if (_gimbals.Length > 0 && _gimbals[0]?.LblGimbalPos != null) _gimbals[0].LblGimbalPos.Text = GimbalPosText(0);
                        }
                    }
                }
                catch { }
            })); } catch { try { if (_gimbals != null)
                {
                    if (idx == 1) { if (_gimbals.Length > 1 && _gimbals[1]?.LblGimbalPos != null) _gimbals[1].LblGimbalPos.Text = GimbalPosText(1); }
                    else { if (_gimbals.Length > 0 && _gimbals[0]?.LblGimbalPos != null) _gimbals[0].LblGimbalPos.Text = GimbalPosText(0); }
                } } catch { } }
            // Update direction line origins without full rebuild
            UpdateDirectionLine();
        }

        // ─── Marker enter/leave (suppress map panning when hovering) ───────
        private void GMap_OnMarkerEnter(GMapMarker item)
        {
            if (item != _gimbalMarker && item != _gimbalMarker2) return;
            try
            {
                var ctl = FlightData.instance?.gMapControl1;
                if (ctl == null) return;
                // Only engage if mouse is actually close to the marker centre
                var local   = ctl.PointToClient(Control.MousePosition);
                var markPos = ctl.FromLatLngToLocal(item.Position);
                int dx = (int)markPos.X - local.X, dy = (int)markPos.Y - local.Y;
                if (dx * dx + dy * dy > 30 * 30) return;
                if (_markerMouseOver) return;
                _markerMouseOver = true;
                _prevCanDragMap  = ctl.CanDragMap;
                ctl.CanDragMap   = false;
                ctl.Cursor       = Cursors.Hand;
            }
            catch { }
        }

        private void GMap_OnMarkerLeave(GMapMarker item)
        {
            if (item != _gimbalMarker && item != _gimbalMarker2) return;
            try
            {
                var ctl = FlightData.instance?.gMapControl1;
                if (ctl == null) return;
                var local   = ctl.PointToClient(Control.MousePosition);
                var markPos = ctl.FromLatLngToLocal(item.Position);
                int dx = (int)markPos.X - local.X, dy = (int)markPos.Y - local.Y;
                if (dx * dx + dy * dy <= 30 * 30) return; // still inside
                if (!_markerMouseOver) return;
                _markerMouseOver = false;
                ctl.CanDragMap   = _prevCanDragMap;
                ctl.Cursor       = Cursors.Default;
            }
            catch { }
        }

        // ─── UI timer ─────────────────────────────────────────────────────
        private void StartUiTimer()
        {
            _uiTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _uiTimer.Tick += (s, e) =>
            {
                    UpdatePosLabels();
                    UpdateDirectionLine();
                    // Run per-gimbal auto-aim state machines.
                    if (_gimbals != null)
                    {
                        for (int i = 0; i < _gimbals.Length; i++)
                        {
                            var ui = _gimbals[i];
                            if (ui == null) continue;
                            bool shouldAuto = ui.AutoAim;
                            if (!shouldAuto) continue;
                            // Advance state and possibly trigger a new aim
                            string stateMsg = AdvanceAimState(ui);
                            ui.AimTickCount++;
                            if (ui.AimState == AimState.Idle && ui.AimTickCount >= 5)
                            {
                                ui.AimTickCount = 0;
                                if (DoPointAtPlaneForGimbal(i, out string msg))
                                {
                                    if (ui.LblPointStatus != null) { ui.LblPointStatus.Text = msg; ui.LblPointStatus.ForeColor = Color.Green; }
                                }
                                else
                                {
                                    if (ui.LblPointStatus != null) { ui.LblPointStatus.Text = msg; ui.LblPointStatus.ForeColor = Color.OrangeRed; }
                                }
                            }
                            else if (stateMsg != null && ui.LblPointStatus != null)
                            {
                                ui.LblPointStatus.Text = stateMsg; ui.LblPointStatus.ForeColor = Color.DodgerBlue;
                            }
                        }
                    }
            };
            _uiTimer.Start();
        }

        // ─── Update labels ─────────────────────────────────────────────────
        private bool _positionDirty;

        private void OnPositionUpdated()
        {
            _positionDirty = true; // reader thread sets flag; UI timer drains it
        }

        private void UpdatePosLabels()
        {
            if (!_positionDirty) return;
            _positionDirty = false;
            try
            {
                // Primary gimbal (index 0)
                if (_gimbals != null && _gimbals.Length > 0 && _gimbals[0] != null && _gimbals[0].LblPitch != null)
                {
                    int dispPitch0 = _gimbal.Pitch - _gimbals[0].PitchOffset;
                    _gimbals[0].LblPitch.Text = dispPitch0.ToString();
                    _gimbals[0].LblYaw.Text   = ((int)GetAdjustedYaw(0)).ToString();
                    _gimbals[0].LblAux.Text   = _gimbal.AuxRaw.ToString();
                    if (_gimbals[0].LblConnStatus != null)
                    {
                        _gimbals[0].LblConnStatus.Text = _gimbal.IsConnected ? "● Connected" : "Disconnected";
                        _gimbals[0].LblConnStatus.ForeColor = _gimbal.IsConnected ? Color.Green : Color.Gray;
                    }
                }

                // Secondary gimbal (index 1)
                if (_gimbals != null && _gimbals.Length > 1 && _gimbals[1] != null && _gimbals[1].LblPitch != null)
                {
                    int dispPitch1 = _gimbal2.Pitch - _gimbals[1].PitchOffset;
                    _gimbals[1].LblPitch.Text = dispPitch1.ToString();
                    double adjYaw2 = (_gimbal2.Yaw + _yawOffset + 3600.0) % 360.0;
                    _gimbals[1].LblYaw.Text = ((int)adjYaw2).ToString();
                    _gimbals[1].LblAux.Text = _gimbal2.AuxRaw.ToString();
                    if (_gimbals[1].LblConnStatus != null)
                    {
                        _gimbals[1].LblConnStatus.Text = _gimbal2.IsConnected ? "● Connected" : "Disconnected";
                        _gimbals[1].LblConnStatus.ForeColor = _gimbal2.IsConnected ? Color.Green : Color.Gray;
                    }
                }
            }
            catch { }
        }

        // Build minimal per-gimbal controls into the provided tab page and register them
        private void BuildGimbalControls(int idx, TabPage parent)
        {
            if (parent == null) return;
            var ui = new GimbalUI();
            ui.Serial = idx == 0 ? _gimbal : _gimbal2;
            ui.Tab = parent;
            _gimbals[idx] = ui;
            // Initialize per-gimbal pitch offset from saved settings
            try
            {
                var savedKey = idx == 0 ? KEY_PITCH_OFFSET : KEY_PITCH_OFFSET2;
                var sVal = Settings.Instance[savedKey]?.ToString();
                if (!string.IsNullOrEmpty(sVal) && int.TryParse(sVal, out int pofs)) ui.PitchOffset = pofs;
            }
            catch { }
            // Initialize per-gimbal UI position from saved globals (if any)
            if (idx == 0)
            {
                ui.Lat = _gimbalLat;
                ui.Lng = _gimbalLng;
                ui.AltM = _gimbalAltM;
            }
            else
            {
                ui.Lat = _gimbalLat2;
                ui.Lng = _gimbalLng2;
                ui.AltM = _gimbalAltM2;
            }

            int y = 6;
            const int LW = 110, ROW = 28;
            const int BW = 120, BW_SM = 80;

            // Port / baud selectors
            var lblPortL = NewLabel("Port:", 6, y, 40);
            var cmbPort = new ComboBox { Left = 6 + 44, Top = y, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
            try { cmbPort.Items.AddRange(SerialPort.GetPortNames()); } catch { }
            var savedPortKey = idx == 0 ? KEY_PORT : KEY_PORT2;
            var savedPort = Settings.Instance[savedPortKey]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(savedPort) && cmbPort.Items.Contains(savedPort)) cmbPort.SelectedItem = savedPort;
            else if (cmbPort.Items.Count > 0) cmbPort.SelectedIndex = 0;

            var lblBaudL = NewLabel("Baud:", 6 + 44 + 150, y, 40);
            var cmbBaud = new ComboBox { Left = 6 + 44 + 150 + 44, Top = y, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbBaud.Items.AddRange(new object[] { "9600", "57600", "115200", "230400" });
            var savedBaudKey = idx == 0 ? KEY_BAUD : KEY_BAUD2;
            var savedBaud = Settings.Instance[savedBaudKey]?.ToString() ?? Settings.Instance[KEY_BAUD]?.ToString() ?? "115200";
            cmbBaud.SelectedItem = cmbBaud.Items.Contains(savedBaud) ? (object)savedBaud : "115200";

            var btnConnect = new Button { Left = 6 + 44 + 150 + 140, Top = y, Width = 80, Text = "Connect" };
            var btnDisconnect = new Button { Left = 6 + 44 + 150 + 140 + 86, Top = y, Width = 80, Text = "Disconnect" };
            var lblConn = NewLabel("Not connected", 6 + 44 + 150 + 140 + 172, y, 160);
            lblConn.ForeColor = Color.Gray;
            parent.Controls.Add(lblPortL); parent.Controls.Add(cmbPort); parent.Controls.Add(lblBaudL); parent.Controls.Add(cmbBaud);
            parent.Controls.Add(btnConnect); parent.Controls.Add(btnDisconnect); parent.Controls.Add(lblConn);

            btnConnect.Click += (s, e) =>
            {
                try
                {
                    string port = cmbPort.SelectedItem?.ToString();
                    if (string.IsNullOrEmpty(port)) { lblConn.Text = "Select a port"; lblConn.ForeColor = Color.Red; return; }
                    int baud = int.Parse(cmbBaud.SelectedItem?.ToString() ?? "115200");
                    ui.Serial.Connect(port, baud);
                    lblConn.Text = "● Connected"; lblConn.ForeColor = Color.Green;
                    Settings.Instance[savedPortKey] = port;
                    Settings.Instance[savedBaudKey] = baud.ToString();
                    try { Settings.Instance.Save(); } catch { }
                }
                catch (Exception ex) { lblConn.Text = "Error: " + ex.Message; lblConn.ForeColor = Color.Red; }
            };
            btnDisconnect.Click += (s, e) => { ui.Serial.Disconnect(); lblConn.Text = "Disconnected"; lblConn.ForeColor = Color.Gray; };

            var chkRxOnly = new CheckBox
            {
                Left     = 6,
                Top      = y,
                Width    = 200,
                Text     = "RX only (no TX to gimbal)",
                Checked  = ui.RxOnly,
            };
            chkRxOnly.CheckedChanged += (s, e) => { ui.RxOnly = chkRxOnly.Checked; ui.Serial.RxOnly = ui.RxOnly; };
            parent.Controls.Add(chkRxOnly);
            y += ROW + 4;

            y += ROW;

            var lblPitchVal = new Label { Left = 6 + LW, Top = y, Width = 60, Text = "—", Font = new Font("Arial", 13, FontStyle.Bold), ForeColor = Color.FromArgb(0, 85, 204) };
            var lblPitchL = NewLabel("Pitch:", 6, y, LW);
            parent.Controls.Add(lblPitchL); parent.Controls.Add(lblPitchVal);
            y += ROW;

            var lblYawVal = new Label { Left = 6 + LW, Top = y, Width = 60, Text = "—", Font = new Font("Arial", 13, FontStyle.Bold), ForeColor = Color.FromArgb(204, 85, 0) };
            var lblYawL = NewLabel("Yaw:", 6, y, LW);
            parent.Controls.Add(lblYawL); parent.Controls.Add(lblYawVal);
            y += ROW;

            var lblAuxVal = NewLabel("—", 6 + LW, y, 50);
            var lblAuxL = NewLabel("Aux:", 6, y, LW);
            parent.Controls.Add(lblAuxL); parent.Controls.Add(lblAuxVal);

            ui.LblConnStatus = lblConn;
            ui.LblPitch = lblPitchVal;
            ui.LblYaw = lblYawVal;
            ui.LblAux = lblAuxVal;
            y += ROW;

            // Yaw offset (per-gimbal)
            var lblYawOff = NewLabel("Yaw Offset (°):", 6, y, 110);
            var txtYawOff = new TextBox { Left = 6 + 114, Top = y, Width = 55, Text = ui.YawOffset.ToString() };
            txtYawOff.TextChanged += (s, e) => { if (int.TryParse(txtYawOff.Text.Trim(), out int ofs)) ui.YawOffset = ofs; };
            parent.Controls.Add(lblYawOff); parent.Controls.Add(txtYawOff);
            y += ROW + 4;

            // Pitch offset (per-gimbal)
            var lblPitchOff = NewLabel("Pitch Offset (°):", 6, y, 110);
            var txtPitchOff = new TextBox { Left = 6 + 114, Top = y, Width = 55, Text = ui.PitchOffset.ToString() };
            var btnCalPitch = new Button { Left = 6 + 114 + 62, Top = y - 4, Width = 80, Text = "Cal Pitch" };
            txtPitchOff.TextChanged += (s, e) => { if (int.TryParse(txtPitchOff.Text.Trim(), out int pofs)) ui.PitchOffset = pofs; };
            btnCalPitch.Click += (s, e) => {
                try
                {
                    int cur = ui?.Serial?.Pitch ?? 0;
                    ui.PitchOffset = cur;
                    txtPitchOff.Text = cur.ToString();
                    SaveSettings();
                }
                catch { }
            };
            parent.Controls.Add(lblPitchOff); parent.Controls.Add(txtPitchOff); parent.Controls.Add(btnCalPitch);
            y += ROW + 4;

            // ── Section: Native GOTO (0x1C) ─────────────────────────
            AddSectionLabel(parent, "Native GOTO (0x1C)", ref y);
            var lblTP = NewLabel("Target Pitch:", 6, y, LW);
            var txtPitch = new TextBox { Left = 6 + LW, Top = y, Width = 60 };
            var lblTY = NewLabel("Target Yaw:", 6 + LW + 70, y, LW);
            var txtYaw = new TextBox { Left = 6 + 2*LW + 70, Top = y, Width = 60 };
            var lblSpeedNative = NewLabel("Speed:", 6 + 2*LW + 140, y, 50);
            var txtSpeed = new TextBox { Left = 6 + 2*LW + 192, Top = y, Width = 50, Text = "50" };
            parent.Controls.Add(lblTP); parent.Controls.Add(txtPitch);
            parent.Controls.Add(lblTY); parent.Controls.Add(txtYaw);
            parent.Controls.Add(lblSpeedNative); parent.Controls.Add(txtSpeed);
            y += ROW;

            var btnGoPitch = new Button { Left = 6, Top = y, Width = BW_SM, Text = "▶ Go Pitch" };
            var btnGoYaw = new Button { Left = 6 + BW_SM + 4, Top = y, Width = BW_SM, Text = "▶ Go Yaw" };
            var btnCancel = new Button { Left = 6 + 2*(BW_SM+4), Top = y, Width = BW_SM, Text = "■ Cancel" };
            var lblGotoSt = NewLabel("", 6 + 3*(BW_SM+4), y, 200);
            lblGotoSt.ForeColor = Color.DimGray;
            parent.Controls.Add(btnGoPitch); parent.Controls.Add(btnGoYaw); parent.Controls.Add(btnCancel); parent.Controls.Add(lblGotoSt);
            ui.LblGotoStatus = lblGotoSt;

            btnGoPitch.Click += (s, e) =>
            {
                try
                {
                    int pitch = int.Parse(txtPitch.Text.Trim());
                    byte speed = (byte)Clamp(int.Parse(txtSpeed.Text.Trim()), 1, 255);
                    int off = ui?.PitchOffset ?? 0;
                    ushort raw = GimbalProtocol.PitchToRaw(pitch + off);
                    ui.Serial.SendPacket(GimbalProtocol.GotoAbsPacket(1, speed, raw));
                    lblGotoSt.Text = $"Sent pitch={pitch} (raw={raw}) spd={speed} (off={off})";
                    lblGotoSt.ForeColor = Color.Green;
                }
                catch (Exception ex) { lblGotoSt.Text = ex.Message; lblGotoSt.ForeColor = Color.Red; }
            };

            btnGoYaw.Click += (s, e) =>
            {
                try
                {
                    ushort raw = (ushort)Clamp(int.Parse(txtYaw.Text.Trim()), 0, 65535);
                    byte speed = (byte)Clamp(int.Parse(txtSpeed.Text.Trim()), 1, 255);
                    ui.Serial.SendPacket(GimbalProtocol.GotoAbsPacket(0, speed, raw));
                    lblGotoSt.Text = $"Sent yaw={raw} spd={speed}";
                    lblGotoSt.ForeColor = Color.Green;
                }
                catch (Exception ex) { lblGotoSt.Text = ex.Message; lblGotoSt.ForeColor = Color.Red; }
            };

            btnCancel.Click += (s, e) => { ui.Serial.SendPacket(GimbalProtocol.STOP_CMD); lblGotoSt.Text = "Stop sent"; lblGotoSt.ForeColor = Color.DimGray; };
            y += ROW + 4;

            // ── Section: Commands ───────────────────────────────────
            AddSectionLabel(parent, "Commands", ref y);
            var btnZero = new Button { Left = 6, Top = y, Width = BW_SM, Text = "Zero" };
            var btnCalib = new Button { Left = 6 + BW_SM + 4, Top = y, Width = BW_SM, Text = "Calibrate" };
            var lblAzLbl = NewLabel("Default Azimuth:", 6 + 2*(BW_SM+4), y, 110);
            var txtAz = new TextBox { Left = 6 + 2*(BW_SM+4) + 114, Top = y, Width = 60, Text = "200" };
            var btnSetAz = new Button { Left = 6 + 2*(BW_SM+4) + 180, Top = y, Width = 80, Text = "Set Az" };
            var lblCmdSt = NewLabel("", 6, y + ROW, 300);
            lblCmdSt.ForeColor = Color.DimGray;
            btnZero.Click += (s, e) => { ui.Serial.SendPacket(GimbalProtocol.ZERO_CMD); lblCmdSt.Text = "Zero sent"; lblCmdSt.ForeColor = Color.Green; };
            btnCalib.Click += (s, e) => { ui.Serial.SendPacket(GimbalProtocol.CALIBRATE_CMD); lblCmdSt.Text = "Calibrate sent"; lblCmdSt.ForeColor = Color.Green; };
            btnSetAz.Click += (s, e) => { try { ushort az = (ushort)Clamp(int.Parse(txtAz.Text.Trim()), 0, 65535); ui.Serial.SendPacket(GimbalProtocol.SetDefaultAzPacket(az)); lblCmdSt.Text = $"Set Az={az} sent"; lblCmdSt.ForeColor = Color.Green; } catch (Exception ex) { lblCmdSt.Text = ex.Message; lblCmdSt.ForeColor = Color.Red; } };
            parent.Controls.Add(btnZero); parent.Controls.Add(btnCalib); parent.Controls.Add(lblAzLbl); parent.Controls.Add(txtAz); parent.Controls.Add(btnSetAz); parent.Controls.Add(lblCmdSt);
            y += ROW + ROW + 4;

            // ── Section: Point at Plane (auto-aim) ───────────────────
            AddSectionLabel(parent, "Point at Plane (auto-aim)", ref y);
            var chkTrackCompanion = new CheckBox
            {
                Left = 6,
                Top = y,
                Width = 420,
                Text = "Track Companion plugin plane icon (uncheck = MAVLink GPS)",
                Checked = ui.TrackCompanion,
            };
            chkTrackCompanion.CheckedChanged += (s, e) => { ui.TrackCompanion = chkTrackCompanion.Checked; };
            parent.Controls.Add(chkTrackCompanion);
            y += ROW;

            var lblGimAltL = NewLabel("Gimbal Alt (m MSL):", 6, y, 140);
            var txtGimAlt = new TextBox { Left = 6 + 144, Top = y, Width = 60,
                Text = ui.AltM.ToString(CultureInfo.InvariantCulture) };
            var btnSaveAlt = new Button { Left = 6 + 214, Top = y, Width = 80, Text = "Save Alt" };
            btnSaveAlt.Click += (s, e) =>
            {
                if (double.TryParse(txtGimAlt.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double alt))
                {
                    ui.AltM = alt;
                    if (idx == 0) _gimbalAltM = alt; else _gimbalAltM2 = alt;
                    SaveSettings();
                    if (ui.LblPointStatus != null) { ui.LblPointStatus.Text = $"Gimbal alt saved: {alt} m"; ui.LblPointStatus.ForeColor = Color.Green; }
                }
            };
            parent.Controls.Add(lblGimAltL); parent.Controls.Add(txtGimAlt); parent.Controls.Add(btnSaveAlt);
            y += ROW;

            var lblPtSpd = NewLabel("GOTO Speed:", 6, y, LW);
            var txtPtSpeed = new TextBox { Left = 6 + LW, Top = y, Width = 50, Text = "50" };
            parent.Controls.Add(lblPtSpd); parent.Controls.Add(txtPtSpeed);
            y += ROW;
            ui.TxtAutoAimSpeed = txtPtSpeed;

            var btnPointOnce2 = new Button { Left = 6, Top = y, Width = BW_SM + 20, Text = "▶ Once" };
            var btnAuto2 = new Button { Left = 6 + BW_SM + 24, Top = y, Width = BW, Text = "⟳ Auto-Track Plane" };
            var lblPoint2 = ui.LblPointStatus ?? NewLabel("", 6 + BW_SM + 24 + BW + 4, y, 200);
            lblPoint2.ForeColor = Color.DimGray;
            btnPointOnce2.Click += (s, e) =>
            {
                try
                {
                    if (!DoPointAtPlaneForGimbal(idx, out string msg, forceAll: true))
                    { lblPoint2.Text = msg; lblPoint2.ForeColor = Color.OrangeRed; }
                    else
                    { lblPoint2.Text = msg; lblPoint2.ForeColor = Color.Green; }
                }
                catch (Exception ex) { lblPoint2.Text = ex.Message; lblPoint2.ForeColor = Color.Red; }
            };
            btnAuto2.Click += (s, e) =>
            {
                ui.AutoAim = !ui.AutoAim;
                btnAuto2.BackColor = ui.AutoAim ? Color.LimeGreen : SystemColors.Control;
                btnAuto2.ForeColor = ui.AutoAim ? Color.Black : SystemColors.ControlText;
                if (lblPoint2 != null)
                {
                    lblPoint2.Text = ui.AutoAim ? "Auto-tracking ON" : "Auto-tracking OFF";
                    lblPoint2.ForeColor = ui.AutoAim ? Color.Green : Color.DimGray;
                }
            };
            parent.Controls.Add(btnPointOnce2); parent.Controls.Add(btnAuto2); parent.Controls.Add(lblPoint2);
            ui.BtnAutoAim = btnAuto2; ui.LblPointStatus = lblPoint2;
            y += ROW + 4;

            // ── Section: Gimbal Map Position ───────────────────────
            AddSectionLabel(parent, "Gimbal Map Position", ref y);
            ui.LblGimbalPos = NewLabel(GimbalPosText(idx), 6, y, 320);
            var btnClearPos = new Button { Left = 330, Top = y, Width = BW_SM, Text = "Clear" };
            btnClearPos.Click += (s, e) =>
            {
                if (idx == 0) { _gimbalLat = double.NaN; _gimbalLng = double.NaN; }
                else { _gimbalLat2 = double.NaN; _gimbalLng2 = double.NaN; }
                SaveSettings();
                ui.LblGimbalPos.Text = GimbalPosText(idx);
                RefreshMapObjects();
            };
            parent.Controls.Add(ui.LblGimbalPos); parent.Controls.Add(btnClearPos);
            y += ROW;

            var hint = NewLabel("Right-click on map → \"Set Gimbal Position Here\"", 6, y, 360);
            hint.ForeColor = Color.Gray;
            parent.Controls.Add(hint);

            var btnPointOnce = new Button { Left = 6, Top = y, Width = 110, Text = "▶ Point Once" };
            var lblPoint = NewLabel("", 6 + 120, y, 320);
            lblPoint.ForeColor = Color.DimGray;
            btnPointOnce.Click += (s, e) =>
            {
                try
                {
                    if (!DoPointAtGimbal(idx, out string pm, forceAll: true))
                    {
                        lblPoint.Text = pm; lblPoint.ForeColor = Color.OrangeRed;
                    }
                    else
                    {
                        lblPoint.Text = pm; lblPoint.ForeColor = Color.Green;
                    }
                }
                catch (Exception ex) { lblPoint.Text = ex.Message; lblPoint.ForeColor = Color.Red; }
            };
            parent.Controls.Add(btnPointOnce); parent.Controls.Add(lblPoint);
            ui.LblPointStatus = lblPoint;
            
            // Per-gimbal Show 0x1B azimuth checkbox
            var chkShow1b = new CheckBox { Left = 6 + 460, Top = y, Width = 220, Text = "Show 0x1B azimuth" , Checked = ui.Show1bAzLine };
            chkShow1b.CheckedChanged += (s, e) => { ui.Show1bAzLine = chkShow1b.Checked; UpdateDirectionLine(); };
            parent.Controls.Add(chkShow1b);
            
            y += ROW;

            // Auto-aim controls (per-gimbal)
            var lblSpd = NewLabel("GOTO Speed:", 6, y, 90);
            var txtSpd = new TextBox { Left = 6 + 100, Top = y, Width = 60, Text = "50" };
            var btnAuto = new Button { Left = 6 + 170, Top = y - 2, Width = 120, Text = "⟳ Auto-Track" };
            parent.Controls.Add(lblSpd); parent.Controls.Add(txtSpd); parent.Controls.Add(btnAuto);
            ui.TxtAutoAimSpeed = txtSpd;
            ui.BtnAutoAim = btnAuto;
            btnAuto.Click += (s, e) =>
            {
                ui.AutoAim = !ui.AutoAim;
                btnAuto.BackColor = ui.AutoAim ? Color.LimeGreen : SystemColors.Control;
                btnAuto.ForeColor = ui.AutoAim ? Color.Black : SystemColors.ControlText;
                if (ui.LblPointStatus != null)
                {
                    ui.LblPointStatus.Text = ui.AutoAim ? "Auto-tracking ON" : "Auto-tracking OFF";
                    ui.LblPointStatus.ForeColor = ui.AutoAim ? Color.Green : Color.DimGray;
                }
            };
        }

        // ─── Auto-aim helper ───────────────────────────────────────────────
        /// <summary>
        /// Checks whether the current axis movement is complete (0x1B silence + position within
        /// deadband) and, if so, sends the next pending axis command.
        /// Returns a status string for display, or null if nothing changed.
        /// </summary>
        private string AdvanceAimState(GimbalUI ui)
        {
            if (ui == null) return null;
            if (ui.AimState == AimState.Idle) return null;

            const double SILENCE_SEC  = 0.5;   // 0x1B must be silent this long after reaching target
            const double POS_DEADBAND = 3.0;   // degrees
            const double TIMEOUT_SEC  = 8.0;   // hard timeout — break out of any stuck state

            // Hard timeout: if movement hasn't completed in TIMEOUT_SEC, reset to Idle
            double elapsed = (double)(Stopwatch.GetTimestamp() - ui.AimStateEnteredTicks) / Stopwatch.Frequency;
            if (elapsed >= TIMEOUT_SEC)
            {
                ui.AimState = AimState.Idle;
                return "⚠ Timed out waiting for gimbal — state reset";
            }

            double silenceSec = ui.Serial?.SecondsSinceLastFrame1b ?? double.MaxValue;
            int frame1bAz = ui.Serial?.Frame1bAz ?? -1;

            if (ui.AimState == AimState.WaitingYaw)
            {
                // Keep pending pitch up-to-date so the latest plane position is used
                TryRefreshPendingPitch(ui);

                // If frame1bAz < 0 the gimbal never emitted a 0x1B (moved too fast or already on target)
                // — rely on silence alone rather than getting stuck forever.
                bool posOk = frame1bAz < 0 || AngleDiff(frame1bAz, ui.TargetYaw) <= POS_DEADBAND;
                bool silent = silenceSec >= SILENCE_SEC;
                if (posOk && silent)
                {
                    if (ui.PendingPitchNeeded)
                    {
                        if (ui.Serial.SendPacket(GimbalProtocol.GotoAbsPacket(1, ui.PendingSpeed, ui.PendingPitchRaw)))
                        {
                            ui.Serial.ResetFrame1bTimer();
                            ui.LastSentPitch = ui.PendingPitchDeg;
                            ui.TargetPitch   = ui.PendingPitchDeg;
                            ui.AimState = AimState.WaitingPitch;
                            ui.AimStateEnteredTicks = Stopwatch.GetTimestamp();
                            return $"Yaw done → sending pitch={ui.PendingPitchRaw}";
                        }
                        else
                        {
                            ui.AimState = AimState.Idle;
                            return "TX failed sending pitch";
                        }
                    }
                    else
                    {
                        ui.AimState = AimState.Idle;
                        return null;
                    }
                }
                return $"Moving yaw → target={ui.TargetYaw:F0}° current={frame1bAz}° silence={silenceSec:F1}s";
            }

            if (ui.AimState == AimState.WaitingPitch)
            {
                int frame1bPitch = ui.Serial?.Frame1bPitch ?? 0;
                int adjFrame1bPitch = frame1bPitch - (ui?.PitchOffset ?? 0);
                bool posOk = Math.Abs(adjFrame1bPitch - ui.TargetPitch) <= POS_DEADBAND;
                bool silent = silenceSec >= SILENCE_SEC;
                if (posOk && silent)
                {
                    ui.AimState = AimState.Idle;
                    return null;
                }
                return $"Moving pitch → target={ui.TargetPitch:F0}° current={adjFrame1bPitch}°";
            }

            return null;
        }

        /// <summary>
        /// Recomputes pitch-to-plane from current telemetry and updates _pendingPitchRaw/Deg.
        /// Called every tick while WaitingYaw so the latest value is always queued.
        /// </summary>
        private void TryRefreshPendingPitch(GimbalUI ui)
        {
            try
            {
                if (ui == null) return;
                if (double.IsNaN(ui.Lat) || double.IsNaN(ui.Lng)) return;
                double planeLat, planeLng, planeAlt;
                if (ui.TrackCompanion)
                {
                    if (!TryGetCompanionPlaneData(out planeLat, out planeLng, out planeAlt)) return;
                }
                else
                {
                    if (MainV2.comPort?.MAV?.cs == null) return;
                    planeLat = MainV2.comPort.MAV.cs.lat;
                    planeLng = MainV2.comPort.MAV.cs.lng;
                    planeAlt = MainV2.comPort.MAV.cs.alt / CurrentState.multiplieralt;
                    if (planeLat == 0.0 && planeLng == 0.0) return;
                }
                double pitchDeg = ComputePitch(ui.Lat, ui.Lng, ui.AltM, planeLat, planeLng, planeAlt);
                int pOff = ui?.PitchOffset ?? 0;
                ui.PendingPitchRaw = GimbalProtocol.PitchToRaw(pitchDeg + pOff);
                ui.PendingPitchDeg = pitchDeg;
                ui.PendingPitchNeeded = true;
            }
            catch { }
        }

        /// <summary>
        /// Get plane position from the Companion plugin plane marker on the GMap overlay.
        /// Lat/lng come from the marker Position; altitude is retrieved via reflection on the plugin instance.
        /// </summary>
        private bool TryGetCompanionPlaneData(out double lat, out double lng, out double alt)
        {
            lat = double.NaN; lng = double.NaN; alt = 0.0;
            try
            {
                var mapCtl = FlightData.instance?.gMapControl1;
                if (mapCtl == null) return false;
                foreach (var overlay in mapCtl.Overlays)
                {
                    foreach (var marker in overlay.Markers)
                    {
                        if (marker.GetType().Name != "GMapMarkerPlaneCustom") continue;
                        lat = marker.Position.Lat;
                        lng = marker.Position.Lng;
                        if (double.IsNaN(lat) || double.IsNaN(lng) || (lat == 0.0 && lng == 0.0))
                            return false;
                        // Retrieve altitude from CompanionPlugin instance via reflection
                        try
                        {
                            var pluginsField = Host.GetType().GetField("plugins",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (pluginsField?.GetValue(Host) is System.Collections.IEnumerable plugins)
                            {
                                foreach (var p in plugins)
                                {
                                    if (p?.GetType().Name != "CompanionPlugin") continue;
                                    var f = p.GetType().GetField("planeAlt",
                                        BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (f != null) alt = (double)f.GetValue(p);
                                    break;
                                }
                            }
                        }
                        catch { }
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>Compute gimbal→plane bearing/pitch and send GOTO packets. Returns false with error in msg on failure.
        /// <paramref name="forceAll"/> bypasses the deadband and always sends both axes.</summary>
        private bool DoPointAtPlane(out string msg, bool forceAll = false)
        {
            // Legacy global call: run auto-aim for gimbal 0
            return DoPointAtPlaneForGimbal(0, out msg, forceAll);
        }

        /// <summary>
        /// Point the specified gimbal (idx 0 or 1) at the plane once. Simpler variant
        /// that sends yaw and pitch immediately and reports a status string.
        /// </summary>
        private bool DoPointAtGimbal(int idx, out string msg, bool forceAll = false)
        {
            msg = "";
            var serial = idx == 0 ? _gimbal : _gimbal2;
            var ui = (_gimbals != null && _gimbals.Length > idx) ? _gimbals[idx] : null;
            if (serial == null || !serial.IsConnected)
            { msg = "Gimbal not connected"; return false; }
            if (serial.RxOnly)
            { msg = "RX-only mode — TX blocked"; return false; }

            double glamLat = idx == 0 ? _gimbalLat : _gimbalLat2;
            double glamLng = idx == 0 ? _gimbalLng : _gimbalLng2;
            double glamAlt = idx == 0 ? _gimbalAltM : _gimbalAltM2;
            if (double.IsNaN(glamLat) || double.IsNaN(glamLng))
            { msg = "Set gimbal position on map first (right-click)"; return false; }

            double planeLat, planeLng, planeAlt;
            if (ui?.TrackCompanion ?? false)
            {
                if (!TryGetCompanionPlaneData(out planeLat, out planeLng, out planeAlt))
                { msg = "Companion plane icon not found on map"; return false; }
            }
            else
            {
                if (MainV2.comPort?.MAV?.cs == null)
                { msg = "No MAV telemetry"; return false; }
                planeLat = MainV2.comPort.MAV.cs.lat;
                planeLng = MainV2.comPort.MAV.cs.lng;
                planeAlt = MainV2.comPort.MAV.cs.alt / CurrentState.multiplieralt;
                if (planeLat == 0.0 && planeLng == 0.0)
                { msg = "No GPS fix on plane"; return false; }
            }

            double bearing  = ComputeBearing(glamLat, glamLng, planeLat, planeLng);
            double pitchDeg = ComputePitch(glamLat, glamLng, glamAlt, planeLat, planeLng, planeAlt);

            byte speed = 50;
            try
            {
                if (ui?.TxtAutoAimSpeed != null)
                    speed = (byte)Clamp(int.Parse(ui.TxtAutoAimSpeed.Text.Trim()), 1, 255);
            }
            catch { }

            int yawOff = ui?.YawOffset ?? _yawOffset;
            double targetYaw = (bearing - yawOff + 3600.0) % 360.0;
            ushort yawRaw    = (ushort)Math.Round(targetYaw);
            int pOff = ui?.PitchOffset ?? 0;
            ushort pitchRaw  = GimbalProtocol.PitchToRaw(pitchDeg + pOff);

            double lastYaw = ui?.LastSentYaw ?? double.NaN;
            double lastPitch = ui?.LastSentPitch ?? double.NaN;

            bool yawChanged   = forceAll || double.IsNaN(lastYaw)   || AngleDiff(targetYaw, lastYaw)   >= AUTO_AIM_DEADBAND_DEG;
            bool pitchChanged = forceAll || double.IsNaN(lastPitch) || Math.Abs(pitchDeg  - lastPitch) >= AUTO_AIM_DEADBAND_DEG;

            if (!yawChanged && !pitchChanged)
            {
                msg = $"On target: Bearing={bearing:F1}° Pitch={pitchDeg:F1}°";
                return true;
            }

            // Send yaw then pitch immediately (no sequential wait in this simplified path)
            if (yawChanged)
            {
                if (!serial.SendPacket(GimbalProtocol.GotoAbsPacket(0, speed, yawRaw)))
                { msg = "TX failed — check connection"; return false; }
                serial.ResetFrame1bTimer();
                if (ui != null) ui.LastSentYaw = targetYaw;
            }

            if (pitchChanged)
            {
                if (!serial.SendPacket(GimbalProtocol.GotoAbsPacket(1, speed, pitchRaw)))
                { msg = "TX failed — check connection"; return false; }
                serial.ResetFrame1bTimer();
                if (ui != null) ui.LastSentPitch = pitchDeg;
            }

            msg = $"Sent yaw={yawRaw} pitch={pitchRaw}";
            return true;
        }

        /// <summary>
        /// Sequential point-at-plane that uses the per-gimbal state machine (yaw then pitch).
        /// Called by the UI timer auto-aim loop for each gimbal.
        /// </summary>
        private bool DoPointAtPlaneForGimbal(int idx, out string msg, bool forceAll = false)
        {
            msg = "";
            var serial = idx == 0 ? _gimbal : _gimbal2;
            var ui = (_gimbals != null && _gimbals.Length > idx) ? _gimbals[idx] : null;
            if (serial == null || !serial.IsConnected)
            { msg = "Gimbal not connected"; return false; }
            if (serial.RxOnly)
            { msg = "RX-only mode — TX blocked"; return false; }
            if (ui != null && ui.AimState != AimState.Idle)
            { msg = $"Moving ({ui.AimState})…"; return true; }

            double glamLat = ui != null ? ui.Lat : (idx == 0 ? _gimbalLat : _gimbalLat2);
            double glamLng = ui != null ? ui.Lng : (idx == 0 ? _gimbalLng : _gimbalLng2);
            double glamAlt = ui != null ? ui.AltM : (idx == 0 ? _gimbalAltM : _gimbalAltM2);
            if (double.IsNaN(glamLat) || double.IsNaN(glamLng))
            { msg = "Set gimbal position on map first (right-click)"; return false; }

            double planeLat, planeLng, planeAlt;
            if (ui?.TrackCompanion ?? false)
            {
                if (!TryGetCompanionPlaneData(out planeLat, out planeLng, out planeAlt))
                { msg = "Companion plane icon not found on map"; return false; }
            }
            else
            {
                if (MainV2.comPort?.MAV?.cs == null)
                { msg = "No MAV telemetry"; return false; }
                planeLat = MainV2.comPort.MAV.cs.lat;
                planeLng = MainV2.comPort.MAV.cs.lng;
                planeAlt = MainV2.comPort.MAV.cs.alt / CurrentState.multiplieralt;
                if (planeLat == 0.0 && planeLng == 0.0)
                { msg = "No GPS fix on plane"; return false; }
            }

            double bearing  = ComputeBearing(glamLat, glamLng, planeLat, planeLng);
            double pitchDeg = ComputePitch(glamLat, glamLng, glamAlt, planeLat, planeLng, planeAlt);

            byte speed = 50;
            try
            {
                if (ui?.TxtAutoAimSpeed != null)
                    speed = (byte)Clamp(int.Parse(ui.TxtAutoAimSpeed.Text.Trim()), 1, 255);
            }
            catch { }

            int yawOff2 = ui?.YawOffset ?? _yawOffset;
            double targetYaw = (bearing - yawOff2 + 3600.0) % 360.0;
            ushort yawRaw    = (ushort)Math.Round(targetYaw);
            int pOff2 = ui?.PitchOffset ?? 0;
            ushort pitchRaw  = GimbalProtocol.PitchToRaw(pitchDeg + pOff2);

            double lastYaw = ui?.LastSentYaw ?? double.NaN;
            double lastPitch = ui?.LastSentPitch ?? double.NaN;

            bool yawChanged   = forceAll || double.IsNaN(lastYaw)   || AngleDiff(targetYaw, lastYaw)   >= AUTO_AIM_DEADBAND_DEG;
            bool pitchChanged = forceAll || double.IsNaN(lastPitch) || Math.Abs(pitchDeg  - lastPitch) >= AUTO_AIM_DEADBAND_DEG;

            if (!yawChanged && !pitchChanged)
            {
                msg = $"On target: Bearing={bearing:F1}° Pitch={pitchDeg:F1}°";
                return true;
            }

            if (yawChanged)
            {
                if (!serial.SendPacket(GimbalProtocol.GotoAbsPacket(0, speed, yawRaw)))
                { msg = "TX failed — check connection"; return false; }
                serial.ResetFrame1bTimer();
                if (ui != null)
                {
                    ui.LastSentYaw = targetYaw;
                    ui.TargetYaw = targetYaw;
                    ui.PendingPitchNeeded = pitchChanged;
                    ui.PendingPitchRaw = pitchRaw;
                    ui.PendingPitchDeg = pitchDeg;
                    ui.PendingSpeed = speed;
                    ui.AimState = AimState.WaitingYaw;
                    ui.AimStateEnteredTicks = Stopwatch.GetTimestamp();
                }
                msg = $"Sent yaw={yawRaw}°, waiting for movement…";
                return true;
            }

            // Only pitch needs updating
            if (!serial.SendPacket(GimbalProtocol.GotoAbsPacket(1, speed, pitchRaw)))
            { msg = "TX failed — check connection"; return false; }
            serial.ResetFrame1bTimer();
            if (ui != null)
            {
                ui.LastSentPitch = pitchDeg;
                ui.TargetPitch = pitchDeg;
                ui.AimState = AimState.WaitingPitch;
                ui.AimStateEnteredTicks = Stopwatch.GetTimestamp();
            }
            msg = $"Sent pitch={pitchRaw} (raw), waiting…";
            return true;
        }

        // ─── Geometry helpers ──────────────────────────────────────────────
        /// <summary>Smallest difference between two angles (0–180°), handles 0/360 wrap.</summary>
        private static double AngleDiff(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        private static double ToRad(double deg) => deg * Math.PI / 180.0;
        private static double ToDeg(double rad) => rad * 180.0 / Math.PI;

        /// <summary>Bearing 0–360° from point 1 to point 2.</summary>
        private static double ComputeBearing(double lat1, double lon1, double lat2, double lon2)
        {
            double φ1 = ToRad(lat1), φ2 = ToRad(lat2);
            double dλ = ToRad(lon2 - lon1);
            double y   = Math.Sin(dλ) * Math.Cos(φ2);
            double x   = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(dλ);
            return (ToDeg(Math.Atan2(y, x)) + 360.0) % 360.0;
        }

        /// <summary>Horizontal distance in metres using the haversine formula.</summary>
        private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0;
            double φ1 = ToRad(lat1), φ2 = ToRad(lat2);
            double dφ = ToRad(lat2 - lat1), dλ = ToRad(lon2 - lon1);
            double a  = Math.Sin(dφ / 2) * Math.Sin(dφ / 2)
                      + Math.Cos(φ1) * Math.Cos(φ2) * Math.Sin(dλ / 2) * Math.Sin(dλ / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        /// <summary>
        /// Elevation angle (signed degrees, + = above horizon) from gimbal to target.
        /// </summary>
        private static double ComputePitch(double gimblat, double gimlng, double gimAlt,
                                           double tgtLat,  double tgtLng,  double tgtAlt)
        {
            double horiz = HaversineMeters(gimblat, gimlng, tgtLat, tgtLng);
            double delta = tgtAlt - gimAlt;
            if (horiz < 0.1) return delta >= 0 ? 90.0 : -90.0;
            return ToDeg(Math.Atan2(delta, horiz));
        }

        /// <summary>
        /// Project a point from origin at bearing (degrees) for distMeters to get
        /// the destination PointLatLng (used for the direction line on the map).
        /// </summary>
        private static PointLatLng ProjectPoint(PointLatLng origin, double bearingDeg, double distMeters)
        {
            const double R = 6371000.0;
            double d   = distMeters / R;
            double lat1 = ToRad(origin.Lat);
            double lon1 = ToRad(origin.Lng);
            double brng = ToRad(bearingDeg);
            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d)
                        + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(brng));
            double lon2 = lon1 + Math.Atan2(Math.Sin(brng) * Math.Sin(d) * Math.Cos(lat1),
                                             Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
            return new PointLatLng(ToDeg(lat2), ToDeg(lon2));
        }

        // ─── UI helpers ────────────────────────────────────────────────────
        private static Label NewLabel(string text, int left, int top, int width)
            => new Label { Left = left, Top = top + 3, Width = width, Text = text, AutoSize = false };

        private static void AddSectionLabel(Control parent, string text, ref int y)
        {
            var sep = new Label
            {
                Left      = 6,
                Top       = y,
                Width     = 500,
                Height    = 2,
                BorderStyle = BorderStyle.Fixed3D,
            };
            var lbl = new Label
            {
                Left      = 6,
                Top       = y + 4,
                AutoSize  = true,
                Text      = text,
                Font      = new Font("Arial", 9, FontStyle.Bold),
            };
            parent.Controls.Add(sep);
            parent.Controls.Add(lbl);
            y += 22;
        }

        private string GimbalPosText()
        {
            if (double.IsNaN(_gimbalLat) || double.IsNaN(_gimbalLng))
                return "Gimbal position: not set";
            return $"Gimbal: {_gimbalLat:F6}, {_gimbalLng:F6}  alt={_gimbalAltM:F1}m";
        }

        private string GimbalPosText(int idx)
        {
            if (idx == 0)
            {
                if (double.IsNaN(_gimbalLat) || double.IsNaN(_gimbalLng))
                    return "Gimbal 1: not set";
                return $"Gimbal 1: {_gimbalLat:F6}, {_gimbalLng:F6}  alt={_gimbalAltM:F1}m";
            }
            else
            {
                if (double.IsNaN(_gimbalLat2) || double.IsNaN(_gimbalLng2))
                    return "Gimbal 2: not set";
                return $"Gimbal 2: {_gimbalLat2:F6}, {_gimbalLng2:F6}  alt={_gimbalAltM2:F1}m";
            }
        }

        private static int Clamp(int v, int min, int max)
            => v < min ? min : v > max ? max : v;

        private static Bitmap GetAntennaTrackerIcon(int size)
        {
            try
            {
                // Resources is internal to MissionPlanner — load the image via ResourceManager reflection
                var src = default(System.Drawing.Image);
                try
                {
                    var rm = new System.Resources.ResourceManager(
                        "MissionPlanner.Properties.Resources",
                        typeof(MainV2).Assembly);
                    src = rm.GetObject("Antenna_Tracker_01") as System.Drawing.Image;
                }
                catch { }
                if (src != null)
                {
                    var bmp = new Bitmap(size, size);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.Clear(Color.Transparent);
                        g.DrawImage(src, 0, 0, size, size);
                    }
                    return bmp;
                }
            }
            catch { }
            // Fallback: circle icon
            var fb = new Bitmap(size, size);
            using (var g = Graphics.FromImage(fb))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                int pad = Math.Max(2, size / 8);
                using (var b = new SolidBrush(Color.RoyalBlue))
                    g.FillEllipse(b, pad, pad, size - pad * 2, size - pad * 2);
                using (var pen = new Pen(Color.White, 1.5f))
                    g.DrawEllipse(pen, pad, pad, size - pad * 2 - 1, size - pad * 2 - 1);
            }
            return fb;
        }

        // ─── Global mouse filter for drag support ─────────────────────────
        private class GlobalMapMouseFilter : IMessageFilter
        {
            private const int WM_LBUTTONDOWN = 0x0201;
            private const int WM_MOUSEMOVE   = 0x0200;
            private const int WM_LBUTTONUP   = 0x0202;

            private readonly GimbalRS485Plugin _p;
            private int _draggingIndex = -1;
            public GlobalMapMouseFilter(GimbalRS485Plugin p) { _p = p; }

            public bool PreFilterMessage(ref Message m)
            {
                try
                {
                    if (_p == null || FlightData.instance?.gMapControl1 == null) return false;
                    var ctl = FlightData.instance.gMapControl1;

                    if (m.Msg == WM_LBUTTONDOWN)
                    {
                        // Figure out which marker (if any) is under the cursor
                        var local = ctl.PointToClient(Control.MousePosition);
                        if (_p._gimbalMarker != null)
                        {
                            var markPos = ctl.FromLatLngToLocal(_p._gimbalMarker.Position);
                            int dx = (int)markPos.X - local.X, dy = (int)markPos.Y - local.Y;
                            if (dx * dx + dy * dy <= 20 * 20)
                            {
                                _draggingIndex = 0;
                            }
                        }
                        if (_draggingIndex < 0 && _p._gimbalMarker2 != null)
                        {
                            var markPos2 = ctl.FromLatLngToLocal(_p._gimbalMarker2.Position);
                            int dx2 = (int)markPos2.X - local.X, dy2 = (int)markPos2.Y - local.Y;
                            if (dx2 * dx2 + dy2 * dy2 <= 20 * 20)
                            {
                                _draggingIndex = 1;
                            }
                        }

                        if (_draggingIndex >= 0)
                        {
                            _p._isDraggingGimbal = true;
                            _p._prevCanDragMap   = ctl.CanDragMap;
                            ctl.CanDragMap       = false;
                            ctl.Cursor           = Cursors.Hand;
                            return true;
                        }
                    }
                    else if (m.Msg == WM_MOUSEMOVE && _p._isDraggingGimbal && _draggingIndex >= 0)
                    {
                        var local = ctl.PointToClient(Control.MousePosition);
                        var p     = ctl.FromLocalToLatLng(local.X, local.Y);
                        if (_draggingIndex == 0 && _p._gimbalMarker != null)
                        {
                            _p._gimbalMarker.Position = p;
                            if (_p._directionLine != null && _p._directionLine.Points.Count >= 2)
                            {
                                var endPt = ProjectPoint(p, _p.GetAdjustedYaw(0), 3000);
                                _p._directionLine.Points[0] = p;
                                _p._directionLine.Points[1] = endPt;
                                try { _p._directionLine.Overlay?.Control?.UpdateRouteLocalPosition(_p._directionLine); } catch { }
                            }
                        }
                        else if (_draggingIndex == 1 && _p._gimbalMarker2 != null)
                        {
                            _p._gimbalMarker2.Position = p;
                            if (_p._directionLine2 != null && _p._directionLine2.Points.Count >= 2)
                            {
                                double adjYaw2 = (_p._gimbal2.Yaw + _p._yawOffset + 3600.0) % 360.0;
                                var endPt2 = ProjectPoint(p, adjYaw2, 3000);
                                _p._directionLine2.Points[0] = p;
                                _p._directionLine2.Points[1] = endPt2;
                                try { _p._directionLine2.Overlay?.Control?.UpdateRouteLocalPosition(_p._directionLine2); } catch { }
                            }
                        }
                        try { ctl.Refresh(); } catch { }
                        return true;
                    }
                    else if (m.Msg == WM_LBUTTONUP && _p._isDraggingGimbal && _draggingIndex >= 0)
                    {
                        int idx = _draggingIndex;
                        _draggingIndex = -1;
                        _p._isDraggingGimbal = false;
                        var local = ctl.PointToClient(Control.MousePosition);
                        var p     = ctl.FromLocalToLatLng(local.X, local.Y);
                        if (idx == 0 && _p._gimbalMarker != null) _p._gimbalMarker.Position = p;
                        if (idx == 1 && _p._gimbalMarker2 != null) _p._gimbalMarker2.Position = p;
                        ctl.CanDragMap = _p._prevCanDragMap;
                        ctl.Cursor     = Cursors.Default;
                        _p.OnGimbalMarkerDropped(p, idx);
                        try { ctl.Refresh(); } catch { }
                        return true;
                    }
                }
                catch { }
                return false;
            }
        }
    }
}
