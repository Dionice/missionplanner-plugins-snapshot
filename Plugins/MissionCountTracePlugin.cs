using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using MissionPlanner;

namespace MissionPlanner.plugins
{
    public class MissionCountTracePlugin : MissionPlanner.Plugin.Plugin
    {
        private const uint MissionCountMessageId = 44;
        private const int MaxDetailedEntries = 200;

        private ToolStripMenuItem _menuItem;
        private string _logFilePath;
        private int _entryCount;
        private bool _subscribed;

        public override string Name
        {
            get { return "MISSION_COUNT Stack Trace"; }
        }

        public override string Version
        {
            get { return "0.1"; }
        }

        public override string Author
        {
            get { return "GitHub Copilot"; }
        }

        public override bool Init()
        {
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "mission_count_trace.log");
            EnsureLogDirectory();
            WriteLog("=== MissionCountTracePlugin initialized ===");
            return true;
        }

        public override bool Loaded()
        {
            _menuItem = new ToolStripMenuItem("Open MISSION_COUNT trace log");
            _menuItem.Click += MenuItemOnClick;
            Host.FDMenuMap.Items.Add(_menuItem);

            Subscribe();
            return true;
        }

        public override bool Loop()
        {
            if (!_subscribed)
            {
                Subscribe();
            }

            return true;
        }

        public override bool Exit()
        {
            Unsubscribe();

            if (_menuItem != null)
            {
                _menuItem.Click -= MenuItemOnClick;
                _menuItem.Dispose();
                _menuItem = null;
            }

            WriteLog("=== MissionCountTracePlugin exited ===");
            return true;
        }

        private void Subscribe()
        {
            if (_subscribed || MainV2.comPort == null)
            {
                return;
            }

            try
            {
                MainV2.comPort.OnPacketSent += OnPacketSent;
                MainV2.comPort.OnPacketReceived += OnPacketReceived;
                _subscribed = true;
                WriteLog("Subscribed to MainV2.comPort.OnPacketSent and OnPacketReceived");
                try
                {
                    WriteLog("MainV2.comPort type: " + MainV2.comPort.GetType().FullName);
                }
                catch (Exception ex)
                {
                    WriteLog("Failed to read comPort type: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                WriteLog("Subscribe failed: " + ex.Message);
            }
        }

        private void Unsubscribe()
        {
            if (!_subscribed || MainV2.comPort == null)
            {
                return;
            }

            try
            {
                MainV2.comPort.OnPacketSent -= OnPacketSent;
                MainV2.comPort.OnPacketReceived -= OnPacketReceived;
                _subscribed = false;
                WriteLog("Unsubscribed from MainV2.comPort.OnPacketSent and OnPacketReceived");
            }
            catch (Exception ex)
            {
                WriteLog("Unsubscribe failed: " + ex.Message);
            }
        }

        private void OnPacketSent(object sender, MAVLink.MAVLinkMessage message)
        {
            if (message == null || message.msgid != MissionCountMessageId)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "[{0}] Outbound msgid={1} name={2} sysid={3} compid={4} seq={5} len={6}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                message.msgid,
                SafeMessageName(message),
                message.sysid,
                message.compid,
                message.seq,
                message.Length));

            TryAppendMissionCountDetails(builder, message);

            if (_entryCount < MaxDetailedEntries)
            {
                builder.AppendLine("Stack:");
                builder.AppendLine(new StackTrace(true).ToString());
            }
            else if (_entryCount == MaxDetailedEntries)
            {
                builder.AppendLine("Stack logging limit reached; further entries omit stack traces.");
            }

            builder.AppendLine();
            _entryCount++;
            WriteLog(builder.ToString());
        }

        private void OnPacketReceived(object sender, MAVLink.MAVLinkMessage message)
        {
            try
            {
                if (message == null)
                    return;

                // Log brief info for all received/sent packets to verify events are firing.
                WriteLog(string.Format(CultureInfo.InvariantCulture,
                    "Event: OnPacketReceived msgid={0} name={1} sysid={2} compid={3} seq={4}",
                    message.msgid, SafeMessageName(message), message.sysid, message.compid, message.seq));

                // If we see msgid 44 here, also append full decode/stack for diagnostics.
                if (message.msgid == MissionCountMessageId)
                {
                    var builder = new StringBuilder();
                    builder.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "[{0}] Inbound msgid={1} name={2} sysid={3} compid={4} seq={5} len={6}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        message.msgid,
                        SafeMessageName(message),
                        message.sysid,
                        message.compid,
                        message.seq,
                        message.Length));

                    TryAppendMissionCountDetails(builder, message);
                    builder.AppendLine("Stack:");
                    builder.AppendLine(new StackTrace(true).ToString());
                    builder.AppendLine();
                    WriteLog(builder.ToString());
                }
            }
            catch (Exception ex)
            {
                WriteLog("OnPacketReceived handler error: " + ex.Message);
            }
        }

        private static string SafeMessageName(MAVLink.MAVLinkMessage message)
        {
            try
            {
                return message.msgtypename;
            }
            catch
            {
                return "unknown";
            }
        }

        private static void TryAppendMissionCountDetails(StringBuilder builder, MAVLink.MAVLinkMessage message)
        {
            try
            {
                var missionCount = message.ToStructure<MAVLink.mavlink_mission_count_t>();
                var details = string.Format(CultureInfo.InvariantCulture,
                    "MISSION_COUNT target={0}/{1} count={2} mission_type={3}",
                    missionCount.target_system,
                    missionCount.target_component,
                    missionCount.count,
                    missionCount.mission_type);

                var opaqueIdField = typeof(MAVLink.mavlink_mission_count_t).GetField("opaque_id");
                if (opaqueIdField != null)
                {
                    var opaqueId = opaqueIdField.GetValue(missionCount);
                    details += string.Format(CultureInfo.InvariantCulture, " opaque_id={0}", opaqueId);
                }

                builder.AppendLine(details);
            }
            catch (Exception ex)
            {
                builder.AppendLine("MISSION_COUNT decode failed: " + ex.Message);
            }
        }

        private void MenuItemOnClick(object sender, EventArgs e)
        {
            try
            {
                EnsureLogDirectory();

                if (!File.Exists(_logFilePath))
                {
                    WriteLog("Trace log created by menu request.");
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = _logFilePath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to open trace log: " + ex.Message, Name, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void EnsureLogDirectory()
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private void WriteLog(string text)
        {
            try
            {
                EnsureLogDirectory();
                File.AppendAllText(_logFilePath, text + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}