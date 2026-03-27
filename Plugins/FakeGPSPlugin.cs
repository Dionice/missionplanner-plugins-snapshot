using System;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner;
using MissionPlanner.Utilities;
using System.Diagnostics;

namespace FakeGPS
{
    public class FakeGPSPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "FakeGPS"; } }
        public override string Version { get { return "0.2"; } }
        public override string Author { get { return "Dionice"; } }

        // settings
        private int nsats = 16;
        private double lat = -35.363261;
        private double lon = 149.165230;
        private double alt = 584.0;
        private double yaw = 0.0; // degrees
        private double rate = 5.0; // Hz
        private float hdop = 0.2f;

        private DateTime lastSend = DateTime.MinValue;
        private bool enabled = false;
        private bool continuous = false;

        public override bool Init()
        {
            // run the Loop() method regularly; choose a reasonably fast loop
            this.loopratehz = 10;
            return true;
        }

        public override bool Loaded()
        {
            // load persisted settings (if present)
            try
            {
                // enabled = Settings.Instance.GetBoolean("FakeGPS_enabled", enabled);
                nsats = Settings.Instance.GetInt32("FakeGPS_nsats", nsats);
                lat = Settings.Instance.GetDouble("FakeGPS_lat", lat);
                lon = Settings.Instance.GetDouble("FakeGPS_lon", lon);
                alt = Settings.Instance.GetDouble("FakeGPS_alt", alt);
                yaw = Settings.Instance.GetDouble("FakeGPS_yaw", yaw);
                rate = Settings.Instance.GetDouble("FakeGPS_rate", rate);
                continuous = Settings.Instance.GetBoolean("FakeGPS_continuous", false);
                hdop = (float)Settings.Instance.GetDouble("FakeGPS_hdop", hdop);
            }
            catch { }

            // create menu items
            var root = new ToolStripMenuItem("FakeGPS");
            var setpos = new ToolStripMenuItem("Set Position / Settings");
            setpos.Click += (s, e) => { ShowSettingsDialog(); };
            root.DropDownItems.Add(setpos);

            var toggle = new ToolStripMenuItem("Enabled");
            toggle.Checked = enabled;
            toggle.CheckOnClick = true;
            toggle.CheckedChanged += (s, e) =>
            {
                enabled = toggle.Checked;
            };
            root.DropDownItems.Add(toggle);

            // Continuous send toggle (persistent)
            var contItem = new ToolStripMenuItem("Continuous send") { Checked = continuous, CheckOnClick = true };
            contItem.CheckedChanged += (s, e) => {
                continuous = contItem.Checked;
                try { Settings.Instance["FakeGPS_continuous"] = continuous ? "1" : "0"; Settings.Instance.Save(); } catch { }
            };
            root.DropDownItems.Add(contItem);

            // add context menu actions on the flightdata map
            try
            {
                var setHere = new ToolStripMenuItem("Носій напевно тут");
                setHere.Click += (s, e) => {
                    var p = Host.FDMenuMapPosition;
                    lat = p.Lat;
                    lon = p.Lng;
                    if (enabled)
                    {
                        SendGpsInputOnce();
                    }
                };

                // insert the context item at the top of the menu so it appears first
                Host.FDMenuMap.Items.Insert(0, setHere);

                // add the plugin root menu (insert after the context item)
                Host.FDMenuMap.Items.Insert(2, root);
            }
            catch { }
            return true;
        }

        private void SendGpsInputOnce()
        {
            // ensure there's an open MAVLink connection
            if (MainV2.comPort == null || MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                return;

            // build and send same as Loop()
            DateTimeOffset gpsEpoch = new DateTimeOffset(1980, 1, 6, 0, 0, 0, TimeSpan.Zero);
            var span = DateTimeOffset.UtcNow - gpsEpoch;
            ushort week = (ushort)(span.TotalDays / 7);
            uint weekMs = (uint)(span.TotalMilliseconds - (week * 7UL * 24UL * 3600UL * 1000UL));
            ulong time_usec = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);

            byte fix_type = (byte)(nsats >= 6 ? 3 : 1);
            int lat_e7 = (int)(lat * 1e7);
            int lon_e7 = (int)(lon * 1e7);

            float hdop = this.hdop;
            float vdop = 1.0f;
            float vn = 0.0f, ve = 0.0f, vd = 0.0f;
            float speed_accuracy = 1.0f, horiz_accuracy = 1.0f, vert_accuracy = 1.0f;

            ushort ignore_flags = 0;
            byte gps_id = 0;
            byte satellites_visible = (byte)Math.Max(0, Math.Min(255, nsats));
            ushort yaw100 = (ushort)(yaw * 100.0);

            var pkt = MAVLink.mavlink_gps_input_t.PopulateXMLOrder(time_usec, gps_id, ignore_flags, weekMs, week, fix_type,
                lat_e7, lon_e7, (float)alt, hdop, vdop, vn, ve, vd, speed_accuracy, horiz_accuracy, vert_accuracy, satellites_visible, yaw100);

            try
            {
                MainV2.comPort.sendPacket(pkt, MainV2.comPort.sysidcurrent, MainV2.comPort.compidcurrent);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("FakeGPS send failed: " + ex.Message);
            }
        }

        private void ShowSettingsDialog()
        {
            var f = new Form();
            f.Text = "FakeGPS Settings";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(340, 240);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;

            int rowHeight = 30;
            int labelLeft = 10;
            int boxLeft = 160;
            int top = 10;

            void addRow(string labelText, string boxText, out TextBox box)
            {
                var l = new Label() { Left = labelLeft, Top = top + 6, Width = 140, Text = labelText };
                box = new TextBox() { Left = boxLeft, Top = top, Width = 150, Text = boxText };
                f.Controls.Add(l);
                f.Controls.Add(box);
                top += rowHeight;
            }

            TextBox latBox, lonBox, altBox, nsatsBox, yawBox, rateBox, hdopBox;
            addRow("Latitude:", lat.ToString(System.Globalization.CultureInfo.InvariantCulture), out latBox);
            addRow("Longitude:", lon.ToString(System.Globalization.CultureInfo.InvariantCulture), out lonBox);
            addRow("Altitude (m):", alt.ToString(System.Globalization.CultureInfo.InvariantCulture), out altBox);
            addRow("Satellites:", nsats.ToString(), out nsatsBox);
            addRow("Yaw (deg):", yaw.ToString(System.Globalization.CultureInfo.InvariantCulture), out yawBox);
            addRow("Rate (Hz):", rate.ToString(System.Globalization.CultureInfo.InvariantCulture), out rateBox);
            addRow("HDOP:", hdop.ToString(System.Globalization.CultureInfo.InvariantCulture), out hdopBox);

            var ok = new Button() { Text = "OK", Left = 160, Width = 70, Top = top + 6, DialogResult = DialogResult.OK };
            var cancel = new Button() { Text = "Cancel", Left = 240, Width = 70, Top = top + 6, DialogResult = DialogResult.Cancel };
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;

            if (f.ShowDialog(MainV2.instance) == DialogResult.OK)
            {
                double.TryParse(latBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat);
                double.TryParse(lonBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lon);
                double.TryParse(altBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out alt);
                int.TryParse(nsatsBox.Text, out nsats);
                double.TryParse(yawBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out yaw);
                double.TryParse(rateBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rate);
                double hdopVal = this.hdop;
                double.TryParse(hdopBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hdopVal);
                this.hdop = (float)hdopVal;
                try
                {
                    Settings.Instance["FakeGPS_nsats"] = nsats.ToString();
                    Settings.Instance["FakeGPS_lat"] = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance["FakeGPS_lon"] = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance["FakeGPS_alt"] = alt.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance["FakeGPS_yaw"] = yaw.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance["FakeGPS_rate"] = rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance["FakeGPS_hdop"] = this.hdop.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance.Save();
                }
                catch { }
            }

            f.Dispose();
        }

        public override bool Loop()
        {
            if (!enabled) return true;
            if (!continuous) return true;

            // ensure there's an open MAVLink connection
            if (MainV2.comPort == null || MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                return true;

            var now = DateTime.UtcNow;
            if (lastSend != DateTime.MinValue)
            {
                var elapsed = (now - lastSend).TotalSeconds;
                if (elapsed < (1.0 / Math.Max(0.001, rate))) return true;
            }

            lastSend = now;

            // build GPS_INPUT message
            // time in microseconds since GPS epoch
            DateTimeOffset gpsEpoch = new DateTimeOffset(1980, 1, 6, 0, 0, 0, TimeSpan.Zero);
            var span = DateTimeOffset.UtcNow - gpsEpoch;
            ushort week = (ushort)(span.TotalDays / 7);
            uint weekMs = (uint)(span.TotalMilliseconds - (week * 7UL * 24UL * 3600UL * 1000UL));
            ulong time_usec = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);

            byte fix_type = (byte)(nsats >= 6 ? 3 : 1);

            int lat_e7 = (int)(lat * 1e7);
            int lon_e7 = (int)(lon * 1e7);

            float hdop = this.hdop;
            float vdop = 1.0f;
            float vn = 0.0f, ve = 0.0f, vd = 0.0f;
            float speed_accuracy = 1.0f, horiz_accuracy = 1.0f, vert_accuracy = 1.0f;

            ushort ignore_flags = 0;
            byte gps_id = 0;
            byte satellites_visible = (byte)Math.Max(0, Math.Min(255, nsats));
            ushort yaw100 = (ushort)(yaw * 100.0);

            var pkt = MAVLink.mavlink_gps_input_t.PopulateXMLOrder(time_usec, gps_id, ignore_flags, weekMs, week, fix_type,
                lat_e7, lon_e7, (float)alt, hdop, vdop, vn, ve, vd, speed_accuracy, horiz_accuracy, vert_accuracy, satellites_visible, yaw100);

            try
            {
                MainV2.comPort.sendPacket(pkt, MainV2.comPort.sysidcurrent, MainV2.comPort.compidcurrent);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("FakeGPS send failed: " + ex.Message);
            }

            return true;
        }

        public override bool Exit()
        {
            return true;
        }
    }
}
