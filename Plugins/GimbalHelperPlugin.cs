using System;
using System.Windows.Forms;
using System.Diagnostics;
using MissionPlanner;
using MissionPlanner.GCSViews;
using MissionPlanner.Utilities;

namespace GimbalHelper
{
    public class GimbalHelperPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "GimbalHelper"; } }
        public override string Version { get { return "0.1"; } }
        public override string Author { get { return "Dionice"; } }

        private double lastAlt = double.NaN;

        public override bool Init()
        {
            return true;
        }

        public override bool Loaded()
        {
            try
            {
                // restore last altitude if present
                try { lastAlt = Settings.Instance.GetDouble("GimbalHelper_lastROIAlt", double.NaN); } catch { lastAlt = double.NaN; }

                // add single top-level menu item (no wrapper)
                var pointNoAlt = new ToolStripMenuItem("Ціль ретранслятора");
                pointNoAlt.Click += (s, e) => { PointCameraHereUseLastAlt(); };
                Host.FDMenuMap.Items.Insert(1, pointNoAlt);

                // listen for DO_SET_ROI to capture altitude whenever someone uses the original action
                if (MainV2.comPort != null)
                {
                    MainV2.comPort.OnPacketReceived -= OnPacket;
                    MainV2.comPort.OnPacketReceived += OnPacket;
                    MainV2.comPort.OnPacketSent -= OnPacket;
                    MainV2.comPort.OnPacketSent += OnPacket;
                }
            }
            catch { }
            return true;
        }

        private void OnPacket(object sender, MAVLink.MAVLinkMessage message)
        {
            try
            {
                var id = (MAVLink.MAVLINK_MSG_ID)message.msgid;
                switch (id)
                {
                    case MAVLink.MAVLINK_MSG_ID.COMMAND_INT:
                        {
                            var cmd = (MAVLink.mavlink_command_int_t)message.data;
                            if (cmd.command == (ushort)MAVLink.MAV_CMD.DO_SET_ROI)
                            {
                                // z contains altitude (float)
                                double alt = cmd.z;
                                StoreAlt(alt);
                            }
                            break;
                        }

                    case MAVLink.MAVLINK_MSG_ID.COMMAND_LONG:
                        {
                            var cmd = (MAVLink.mavlink_command_long_t)message.data;
                            if (cmd.command == (ushort)MAVLink.MAV_CMD.DO_SET_ROI)
                            {
                                // param7 often holds altitude
                                double alt = cmd.param7;
                                // some senders use param5/6 for lat/lon and param7 for alt
                                StoreAlt(alt);
                            }
                            break;
                        }
                }
            }
            catch { }
        }

        private void StoreAlt(double alt)
        {
            try
            {
                // only store if value is finite
                if (!double.IsNaN(alt) && !double.IsInfinity(alt))
                {
                    lastAlt = alt;
                    Settings.Instance["GimbalHelper_lastROIAlt"] = alt.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance.Save();
                }
            }
            catch { }
        }

        private void PointCameraHereUseLastAlt()
        {
            try
            {
                if (Host.FDMenuMapPosition == null) return;

                double lat = Host.FDMenuMapPosition.Lat; // already degrees
                double lon = Host.FDMenuMapPosition.Lng; // degrees

                double alt = 0.0;
                if (!double.IsNaN(lastAlt)) alt = lastAlt;

                if (MainV2.comPort == null || MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen) return;

                int lat_e7 = (int)(lat * 1e7);
                int lon_e7 = (int)(lon * 1e7);

                // convert altitude using same multiplier as FlightData and send with GLOBAL_RELATIVE_ALT frame
                float altParam = (float)(alt / CurrentState.multiplieralt);

                MainV2.comPort.doCommandInt(
                    (byte)MainV2.comPort.sysidcurrent,
                    (byte)MainV2.comPort.compidcurrent,
                    MAVLink.MAV_CMD.DO_SET_ROI,
                    0, 0, 0, 0,
                    lat_e7,
                    lon_e7,
                    altParam,
                    frame: MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
                );
            }
            catch (Exception ex)
            {
                try { Trace.WriteLine("GimbalHelper send failed: " + ex.Message); } catch { }
            }
        }

        public override bool Exit()
        {
            try
            {
                if (MainV2.comPort != null)
                {
                    MainV2.comPort.OnPacketReceived -= OnPacket;
                    MainV2.comPort.OnPacketSent -= OnPacket;
                }
            }
            catch { }
            return true;
        }
    }
}
