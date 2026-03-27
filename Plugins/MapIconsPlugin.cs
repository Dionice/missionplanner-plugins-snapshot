using System;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner;
using MissionPlanner.Utilities;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;
using MissionPlanner.GCSViews;

namespace MapIcons
{
    public class MapIconsPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "MapIcons"; } }
        public override string Version { get { return "0.1"; } }
        public override string Author { get { return "Dionice"; } }

        private GMapOverlay overlay;
        private GMarkerGoogle homeMarker;
        private GMarkerGoogle cameraMarker;
        private Bitmap homeBitmap;
        private Bitmap cameraBitmap;

        public override bool Init()
        {
            this.loopratehz = 1;
            return true;
        }

        public override bool Loaded()
        {
            try
            {
                // create bitmaps for markers
                try { homeBitmap = CreateHomeIcon(32); } catch { homeBitmap = null; }
                try { cameraBitmap = CreateCameraIcon(32); } catch { cameraBitmap = null; }

                // create overlay and add to FlightData map
                overlay = new GMapOverlay("mapicons");
                FlightData.instance.gMapControl1.Overlays.Add(overlay);

                // restore persisted positions (if any)
                double hlat = Settings.Instance.GetDouble("MapIcons_Home_lat", double.NaN);
                double hlon = Settings.Instance.GetDouble("MapIcons_Home_lon", double.NaN);
                if (!double.IsNaN(hlat) && !double.IsNaN(hlon))
                {
                    var p = new PointLatLng(hlat, hlon);
                    UpdateHomeMarker(p, persist: false);
                }

                double clat = Settings.Instance.GetDouble("MapIcons_Cam_lat", double.NaN);
                double clon = Settings.Instance.GetDouble("MapIcons_Cam_lon", double.NaN);
                if (!double.IsNaN(clat) && !double.IsNaN(clon))
                {
                    var p = new PointLatLng(clat, clon);
                    UpdateCameraMarker(p, persist: false);
                }

                // add simple menu to toggle visibility
                var root = new ToolStripMenuItem("MapIcons");
                var showHome = new ToolStripMenuItem("Show Home") { CheckOnClick = true };
                showHome.Checked = (homeMarker != null);
                showHome.CheckedChanged += (s, e) => {
                    if (homeMarker != null)
                    {
                        homeMarker.IsVisible = showHome.Checked;
                        FlightData.instance.gMapControl1.Refresh();
                    }
                };
                root.DropDownItems.Add(showHome);

                var showCam = new ToolStripMenuItem("Show Camera") { CheckOnClick = true };
                showCam.Checked = (cameraMarker != null);
                showCam.CheckedChanged += (s, e) => {
                    if (cameraMarker != null)
                    {
                        cameraMarker.IsVisible = showCam.Checked;
                        FlightData.instance.gMapControl1.Refresh();
                    }
                };
                root.DropDownItems.Add(showCam);

                Host.FDMenuMap.Items.Insert(4, root);

                // subscribe to MAVLink packets to update markers automatically (both received and sent)
                if (MainV2.comPort != null)
                {
                    MainV2.comPort.OnPacketReceived -= OnPacketReceived;
                    MainV2.comPort.OnPacketReceived += OnPacketReceived;
                    MainV2.comPort.OnPacketSent -= OnPacketSent;
                    MainV2.comPort.OnPacketSent += OnPacketSent;
                }
            }
            catch { }

            return true;
        }

        private void OnPacketReceived(object sender, MAVLink.MAVLinkMessage message)
        {
            try
            {
                var id = (MAVLink.MAVLINK_MSG_ID)message.msgid;
                switch (id)
                {
                    case MAVLink.MAVLINK_MSG_ID.HOME_POSITION:
                        {
                            var pos = (MAVLink.mavlink_home_position_t)message.data;
                            // home lat/lon are in 1e7
                            double lat = pos.latitude / 1e7;
                            double lon = pos.longitude / 1e7;
                            var p = new PointLatLng(lat, lon);
                            FlightData.instance.gMapControl1.BeginInvoke(new Action(() => UpdateHomeMarker(p, persist: true)));
                            break;
                        }

                    case MAVLink.MAVLINK_MSG_ID.COMMAND_INT:
                        {
                            var cmd = (MAVLink.mavlink_command_int_t)message.data;
                            // check for DO_SET_ROI (MAV_CMD 201)
                            if (cmd.command == (ushort)MAVLink.MAV_CMD.DO_SET_ROI)
                            {
                                // command_int uses x/y as int (lat/lon *1e7)
                                double lat = cmd.x / 1e7;
                                double lon = cmd.y / 1e7;
                                var p = new PointLatLng(lat, lon);
                                FlightData.instance.gMapControl1.BeginInvoke(new Action(() => UpdateCameraMarker(p, persist: true)));
                            }
                            break;
                        }

                    case MAVLink.MAVLINK_MSG_ID.COMMAND_LONG:
                        {
                            var cmd = (MAVLink.mavlink_command_long_t)message.data;
                            if (cmd.command == (ushort)MAVLink.MAV_CMD.DO_SET_ROI)
                            {
                                // command_long uses param5/6 for lat/lon (float degrees)
                                double lat = cmd.param5;
                                double lon = cmd.param6;
                                // if these look like 1e7 integers, convert
                                if (Math.Abs(lat) > 1e6) lat = lat / 1e7;
                                if (Math.Abs(lon) > 1e6) lon = lon / 1e7;
                                var p = new PointLatLng(lat, lon);
                                FlightData.instance.gMapControl1.BeginInvoke(new Action(() => UpdateCameraMarker(p, persist: true)));
                            }
                            break;
                        }
                }
            }
            catch { }
        }

        private void OnPacketSent(object sender, MAVLink.MAVLinkMessage message)
        {
            // treat sent packets the same as received for UI purposes
            try
            {
                // reuse the same processing logic
                OnPacketReceived(sender, message);
            }
            catch { }
        }

        private void UpdateHomeMarker(PointLatLng p, bool persist)
        {
            try
            {
                if (overlay == null) return;

                if (homeMarker != null)
                {
                    homeMarker.Position = p;
                }
                else
                {
                    if (homeBitmap != null)
                    {
                        homeMarker = new GMarkerGoogle(p, homeBitmap) { ToolTipText = "Home", ToolTipMode = MarkerTooltipMode.OnMouseOver };
                        homeMarker.Offset = new Point(-homeBitmap.Width / 2, -homeBitmap.Height / 2);
                    }
                    else
                    {
                        homeMarker = new GMarkerGoogle(p, GMarkerGoogleType.red_pushpin) { ToolTipText = "Home", ToolTipMode = MarkerTooltipMode.OnMouseOver };
                    }
                    overlay.Markers.Add(homeMarker);
                }

                FlightData.instance.gMapControl1.Refresh();

                if (persist)
                {
                    try
                    {
                        Settings.Instance["MapIcons_Home_lat"] = p.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Settings.Instance["MapIcons_Home_lon"] = p.Lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Settings.Instance.Save();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void UpdateCameraMarker(PointLatLng p, bool persist)
        {
            try
            {
                if (overlay == null) return;

                if (cameraMarker != null)
                {
                    cameraMarker.Position = p;
                }
                else
                {
                    if (cameraBitmap != null)
                    {
                        cameraMarker = new GMarkerGoogle(p, cameraBitmap) { ToolTipText = "Ціль Ретранслятора", ToolTipMode = MarkerTooltipMode.OnMouseOver };
                        cameraMarker.Offset = new Point(-cameraBitmap.Width / 2, -cameraBitmap.Height / 2);
                    }
                    else
                        cameraMarker = new GMarkerGoogle(p, GMarkerGoogleType.blue_pushpin) { ToolTipText = "Ціль Ретранслятора", ToolTipMode = MarkerTooltipMode.OnMouseOver };
                    overlay.Markers.Add(cameraMarker);
                }

                FlightData.instance.gMapControl1.Refresh();

                if (persist)
                {
                    try
                    {
                        Settings.Instance["MapIcons_Cam_lat"] = p.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Settings.Instance["MapIcons_Cam_lon"] = p.Lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Settings.Instance.Save();
                    }
                    catch { }
                }
            }
            catch { }
        }

        public override bool Loop()
        {
            return true;
        }

        public override bool Exit()
        {
            try
            {
                if (MainV2.comPort != null)
                    MainV2.comPort.OnPacketReceived -= OnPacketReceived;
            }
            catch { }
            return true;
        }

        // create a simple home icon bitmap (green circle with H)
        private Bitmap CreateHomeIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                int pad = Math.Max(2, size/8);
                g.FillEllipse(Brushes.Green, pad, pad, size - pad*2, size - pad*2);
                using (var f = new Font("Arial", size/2, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("H", f, b, new RectangleF(0, 0, size, size), sf);
                }
            }
            return bmp;
        }

        // create a simple camera icon bitmap (red cross)
        private Bitmap CreateCameraIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // draw a bold red cross centered in the icon
                int thickness = Math.Max(2, size / 6);
                using (var pen = new Pen(Color.DarkBlue, thickness) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                {
                    g.DrawLine(pen, thickness, thickness, size - thickness, size - thickness);
                    g.DrawLine(pen, size - thickness, thickness, thickness, size - thickness);
                }
            }
            return bmp;
        }
    }
}
