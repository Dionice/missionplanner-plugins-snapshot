using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MissionPlanner;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;

namespace MissionPlanner.plugins
{
    public class Esp32VtxMavlinkTestPlugin : MissionPlanner.Plugin.Plugin
    {
        private const int Esp32VtxCommandId = 31001;
        private const int RequestMessageCommandId = 512;
        private const int AutopilotVersionMessageId = 148;
        private const byte DefaultTargetSystemId = 1;
        private const byte DefaultTargetComponentId = 68;

        private ToolStripMenuItem _menuItem;
        private RouteTestForm _form;

        public override string Name
        {
            get { return "ESP32 MAVLink Route Test"; }
        }

        public override string Version
        {
            get { return "0.2"; }
        }

        public override string Author
        {
            get { return "GitHub Copilot"; }
        }

        public override bool Init()
        {
            return true;
        }

        public override bool Loaded()
        {
            _menuItem = new ToolStripMenuItem("ESP32 MAVLink Route Test");
            _menuItem.Click += MenuItemOnClick;
            Host.FDMenuMap.Items.Add(_menuItem);
            return true;
        }

        public override bool Exit()
        {
            try
            {
                if (_menuItem != null)
                {
                    _menuItem.Click -= MenuItemOnClick;
                }

                if (_form != null && !_form.IsDisposed)
                {
                    _form.Close();
                    _form.Dispose();
                }
            }
            catch
            {
            }

            return true;
        }

        private void MenuItemOnClick(object sender, EventArgs e)
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new RouteTestForm();
            }

            _form.Show();
            _form.BringToFront();
        }

        private class RouteTestForm : Form
        {
            private const byte DefaultNodeId = 1;
            private const byte DefaultDeviceId = 1;
            private const byte BroadcastNodeId = 255;
            private const byte AllDevicesId = 255;
            private const byte DefaultBand = 1;
            private const byte DefaultChannel = 1;
            private const byte DefaultPowerIndex = 0;
            private const byte DefaultFlags = 0;
            private const byte KeepBandValue = 0;
            private const byte KeepChannelValue = 0;
            private const byte KeepPowerValue = 255;

            private bool _packetHandlerAttached;
            private uint _pingSequence;
            private byte _currentTargetSystem;
            private byte _currentTargetComponent;
            private ushort? _pendingAckCommand;
            private string _pendingAckDescription = string.Empty;
            private readonly NumericUpDown _targetSystem = new NumericUpDown();
            private readonly NumericUpDown _targetComponent = new NumericUpDown();
            private readonly NumericUpDown _nodeId = new NumericUpDown();
            private readonly NumericUpDown _deviceId = new NumericUpDown();
            private readonly NumericUpDown _band = new NumericUpDown();
            private readonly NumericUpDown _channel = new NumericUpDown();
            private readonly NumericUpDown _powerIndex = new NumericUpDown();
            private readonly NumericUpDown _flags = new NumericUpDown();
            private readonly Label _linkStatus = new Label();
            private readonly Label _endpointStatus = new Label();
            private readonly Label _commandStatus = new Label();
            private readonly TextBox _log = new TextBox();

            public RouteTestForm()
            {
                Text = "ESP32 VTX MAVLink";
                Width = 760;
                Height = 640;
                MinimumSize = new Size(680, 520);
                StartPosition = FormStartPosition.CenterScreen;

                var layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.ColumnCount = 2;
                layout.RowCount = 7;
                layout.Padding = new Padding(10);
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                Controls.Add(layout);

                var info = new Label();
                info.AutoSize = true;
                info.MaximumSize = new Size(660, 0);
                info.Text = "Use the preflight buttons to verify routing to the ESP32 companion endpoint with standard MAVLink messages, then send real VTX commands over COMMAND_LONG 31001. With the current firmware the ESP32 should appear under Vehicle 1 as component 68, not as a separate vehicle. VTX sentinels: node 255=broadcast, device 255=all devices, band/channel 0=keep current, power 255=keep current.";
                layout.Controls.Add(info, 0, 0);
                layout.SetColumnSpan(info, 2);

                AddLabeledControl(layout, 1, "Target System", _targetSystem);
                _targetSystem.Minimum = 0;
                _targetSystem.Maximum = 255;
                _targetSystem.Value = ReadSetting("esp32route_targetsys", DefaultTargetSystemId);
                _targetSystem.ValueChanged += (sender, args) => UpdateTargetCache();

                AddLabeledControl(layout, 2, "Target Component", _targetComponent);
                _targetComponent.Minimum = 0;
                _targetComponent.Maximum = 255;
                _targetComponent.Value = ReadSetting("esp32route_targetcomp", DefaultTargetComponentId);
                _targetComponent.ValueChanged += (sender, args) => UpdateTargetCache();

                var vtxPanel = new FlowLayoutPanel();
                vtxPanel.Dock = DockStyle.Fill;
                vtxPanel.AutoSize = true;
                vtxPanel.WrapContents = true;

                ConfigureNumeric(_nodeId, 1, 255, ReadSetting("esp32vtx_nodeid", DefaultNodeId));
                ConfigureNumeric(_deviceId, 1, 255, ReadSetting("esp32vtx_deviceid", DefaultDeviceId));
                ConfigureNumeric(_band, 0, 8, ReadSetting("esp32vtx_band", DefaultBand));
                ConfigureNumeric(_channel, 0, 8, ReadSetting("esp32vtx_channel", DefaultChannel));
                ConfigureNumeric(_powerIndex, 0, 255, ReadSetting("esp32vtx_powerindex", DefaultPowerIndex));
                ConfigureNumeric(_flags, 0, 255, ReadSetting("esp32vtx_flags", DefaultFlags));

                vtxPanel.Controls.Add(BuildInlineLabel("Node"));
                vtxPanel.Controls.Add(_nodeId);
                vtxPanel.Controls.Add(BuildInlineLabel("Device"));
                vtxPanel.Controls.Add(_deviceId);
                vtxPanel.Controls.Add(BuildInlineLabel("Band"));
                vtxPanel.Controls.Add(_band);
                vtxPanel.Controls.Add(BuildInlineLabel("Channel"));
                vtxPanel.Controls.Add(_channel);
                vtxPanel.Controls.Add(BuildInlineLabel("Power"));
                vtxPanel.Controls.Add(_powerIndex);
                vtxPanel.Controls.Add(BuildInlineLabel("Flags"));
                vtxPanel.Controls.Add(_flags);
                layout.Controls.Add(vtxPanel, 0, 3);
                layout.SetColumnSpan(vtxPanel, 2);

                var buttonPanel = new FlowLayoutPanel();
                buttonPanel.Dock = DockStyle.Fill;
                buttonPanel.AutoSize = true;

                var useDefaultTarget = new Button();
                useDefaultTarget.Text = "Use 1/68";
                useDefaultTarget.Click += (sender, args) => ApplyDefaultTarget();

                var useVtxDefaults = new Button();
                useVtxDefaults.Text = "Use VTX defaults";
                useVtxDefaults.Click += (sender, args) => ApplyVtxDefaults();

                var useBroadcastVtx = new Button();
                useBroadcastVtx.Text = "Broadcast VTX";
                useBroadcastVtx.Click += (sender, args) => ApplyBroadcastVtx();

                var keepCurrentValues = new Button();
                keepCurrentValues.Text = "Keep current";
                keepCurrentValues.Click += (sender, args) => ApplyKeepCurrentValues();

                var sendPing = new Button();
                sendPing.Text = "Send PING";
                sendPing.Click += (sender, args) => SendPing();

                var sendBroadcastPing = new Button();
                sendBroadcastPing.Text = "Broadcast PING";
                sendBroadcastPing.Click += (sender, args) => SendBroadcastPing();

                var requestVersion = new Button();
                requestVersion.Text = "Request AUTOPILOT_VERSION";
                requestVersion.Click += (sender, args) => SendAutopilotVersionRequest();

                var sendVtx = new Button();
                sendVtx.Text = "Send VTX command";
                sendVtx.Click += (sender, args) => SendVtxCommand();

                var refresh = new Button();
                refresh.Text = "Refresh link";
                refresh.Click += (sender, args) => UpdateStatusLabels();

                var clearLog = new Button();
                clearLog.Text = "Clear log";
                clearLog.Click += (sender, args) => _log.Clear();

                buttonPanel.Controls.Add(useDefaultTarget);
                buttonPanel.Controls.Add(useVtxDefaults);
                buttonPanel.Controls.Add(useBroadcastVtx);
                buttonPanel.Controls.Add(keepCurrentValues);
                buttonPanel.Controls.Add(sendPing);
                buttonPanel.Controls.Add(sendBroadcastPing);
                buttonPanel.Controls.Add(requestVersion);
                buttonPanel.Controls.Add(sendVtx);
                buttonPanel.Controls.Add(refresh);
                buttonPanel.Controls.Add(clearLog);
                layout.Controls.Add(buttonPanel, 0, 4);
                layout.SetColumnSpan(buttonPanel, 2);

                _commandStatus.AutoSize = true;
                _commandStatus.Text = "No command pending.";
                layout.Controls.Add(_commandStatus, 0, 5);
                layout.SetColumnSpan(_commandStatus, 2);

                var statusPanel = new TableLayoutPanel();
                statusPanel.Dock = DockStyle.Fill;
                statusPanel.ColumnCount = 1;
                statusPanel.RowCount = 3;
                statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                _linkStatus.AutoSize = true;
                _endpointStatus.AutoSize = true;
                _log.Multiline = true;
                _log.Dock = DockStyle.Fill;
                _log.ReadOnly = true;
                _log.ScrollBars = ScrollBars.Vertical;

                statusPanel.Controls.Add(_linkStatus, 0, 0);
                statusPanel.Controls.Add(_endpointStatus, 0, 1);
                statusPanel.Controls.Add(_log, 0, 2);
                layout.Controls.Add(statusPanel, 0, 6);
                layout.SetColumnSpan(statusPanel, 2);

                FormClosing += OnFormClosing;
                Shown += (sender, args) =>
                {
                    UpdateTargetCache();
                    AttachPacketHandler();
                    UpdateStatusLabels();
                    AppendLog("Expect Vehicle 1 -> Comp 68 in MAVLink Inspector when routing is healthy.");
                    AppendLog("Use PING or REQUEST_MESSAGE as preflight, then send VTX command 31001 with the controls above.");
                    AppendLog("Sentinels: node 255=broadcast, device 255=all devices, band/channel 0=keep current, power 255=keep current.");
                };
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (Visible)
                {
                    AttachPacketHandler();
                }
                else
                {
                    DetachPacketHandler();
                }
            }

            private static void AddLabeledControl(TableLayoutPanel layout, int row, string labelText, Control control)
            {
                var label = new Label();
                label.Text = labelText;
                label.TextAlign = ContentAlignment.MiddleLeft;
                label.Dock = DockStyle.Fill;
                label.AutoSize = true;

                control.Dock = DockStyle.Fill;

                layout.Controls.Add(label, 0, row);
                layout.Controls.Add(control, 1, row);
            }

            private static void ConfigureNumeric(NumericUpDown control, int min, int max, int value)
            {
                control.Minimum = min;
                control.Maximum = max;
                control.Width = 60;
                control.Value = Math.Max(min, Math.Min(max, value));
            }

            private static Label BuildInlineLabel(string text)
            {
                return new Label
                {
                    AutoSize = true,
                    Text = text,
                    Padding = new Padding(8, 6, 2, 0),
                };
            }

            private int ReadSetting(string key, int fallback)
            {
                try
                {
                    var raw = Settings.Instance[key];
                    if (raw == null)
                    {
                        return fallback;
                    }

                    int parsed;
                    return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                        ? parsed
                        : fallback;
                }
                catch
                {
                    return fallback;
                }
            }

            private void UpdateTargetCache()
            {
                _currentTargetSystem = (byte)_targetSystem.Value;
                _currentTargetComponent = (byte)_targetComponent.Value;
                SaveSettings();
                UpdateStatusLabels();
            }

            private void ApplyDefaultTarget()
            {
                _targetSystem.Value = DefaultTargetSystemId;
                _targetComponent.Value = DefaultTargetComponentId;
                AppendLog("Target set to Vehicle 1 / Component 68.");
            }

            private void ApplyVtxDefaults()
            {
                _nodeId.Value = DefaultNodeId;
                _deviceId.Value = DefaultDeviceId;
                _band.Value = DefaultBand;
                _channel.Value = DefaultChannel;
                _powerIndex.Value = DefaultPowerIndex;
                _flags.Value = DefaultFlags;
                SaveSettings();
                AppendLog("VTX defaults applied: node=1 device=1 band=1 channel=1 power=0 flags=0.");
            }

            private void ApplyBroadcastVtx()
            {
                _nodeId.Value = BroadcastNodeId;
                _deviceId.Value = AllDevicesId;
                SaveSettings();
                AppendLog("Broadcast VTX applied: node=255 device=255.");
            }

            private void ApplyKeepCurrentValues()
            {
                _band.Value = KeepBandValue;
                _channel.Value = KeepChannelValue;
                _powerIndex.Value = KeepPowerValue;
                SaveSettings();
                AppendLog("Keep-current VTX values applied: band=0 channel=0 power=255.");
            }

            private void UpdateStatusLabels()
            {
                try
                {
                    var open = MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
                    _linkStatus.Text = string.Format(CultureInfo.InvariantCulture,
                        "Link open: {0} | Port: {1}",
                        open ? "yes" : "no",
                        MainV2.comPort != null && MainV2.comPort.BaseStream != null ? MainV2.comPort.BaseStream.PortName : "n/a");

                    _endpointStatus.Text = string.Format(CultureInfo.InvariantCulture,
                        "Expected endpoint in MAVLink Inspector: Vehicle {0} -> Comp {1}",
                        _currentTargetSystem,
                        _currentTargetComponent);
                }
                catch (Exception ex)
                {
                    AppendLog("Status refresh failed: " + ex.Message);
                }
            }

            private void AttachPacketHandler()
            {
                if (_packetHandlerAttached || MainV2.comPort == null)
                {
                    return;
                }

                MainV2.comPort.OnPacketReceived += OnPacketReceived;
                _packetHandlerAttached = true;
            }

            private void DetachPacketHandler()
            {
                if (!_packetHandlerAttached || MainV2.comPort == null)
                {
                    return;
                }

                MainV2.comPort.OnPacketReceived -= OnPacketReceived;
                _packetHandlerAttached = false;
            }

            private void OnPacketReceived(object sender, MAVLink.MAVLinkMessage message)
            {
                try
                {
                    if (message == null)
                    {
                        return;
                    }

                    if (message.sysid != _currentTargetSystem || message.compid != _currentTargetComponent)
                    {
                        return;
                    }

                    if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT)
                    {
                        BeginInvoke((Action)(() =>
                            AppendLog(string.Format(CultureInfo.InvariantCulture,
                                "RX HEARTBEAT from {0}/{1}",
                                message.sysid,
                                message.compid))));
                        return;
                    }

                    if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT)
                    {
                        var text = message.ToStructure<MAVLink.mavlink_statustext_t>();
                        BeginInvoke((Action)(() =>
                            AppendLog(string.Format(CultureInfo.InvariantCulture,
                                "RX STATUSTEXT severity={0} text={1}",
                                text.severity,
                                text.text))));
                        return;
                    }

                    if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PING)
                    {
#pragma warning disable 0612
                        var ping = message.ToStructure<MAVLink.mavlink_ping_t>();
#pragma warning restore 0612
                        BeginInvoke((Action)(() =>
                            AppendLog(string.Format(CultureInfo.InvariantCulture,
                                "RX PING seq={0} target={1}/{2}",
                                ping.seq,
                                ping.target_system,
                                ping.target_component))));
                        return;
                    }

                    if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION)
                    {
                        var version = message.ToStructure<MAVLink.mavlink_autopilot_version_t>();
                        BeginInvoke((Action)(() =>
                        {
                            if (_pendingAckCommand == RequestMessageCommandId)
                            {
                                ClearPendingAck("AUTOPILOT_VERSION received.");
                            }
                            AppendLog(string.Format(CultureInfo.InvariantCulture,
                                "RX AUTOPILOT_VERSION fw=0x{0:X8} caps=0x{1:X8} vendor={2} product={3}",
                                version.flight_sw_version,
                                (uint)version.capabilities,
                                version.vendor_id,
                                version.product_id));
                        }));
                        return;
                    }

                    if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_ACK)
                    {
                        var ack = message.ToStructure<MAVLink.mavlink_command_ack_t>();
                        BeginInvoke((Action)(() =>
                        {
                            if (_pendingAckCommand.HasValue && ack.command == _pendingAckCommand.Value)
                            {
                                ClearPendingAck(string.Format(CultureInfo.InvariantCulture,
                                    "ACK {0} result={1}",
                                    _pendingAckDescription,
                                    DescribeMavResult(ack.result)));
                            }

                            AppendLog(string.Format(CultureInfo.InvariantCulture,
                                "RX COMMAND_ACK command={0} result={1} requester={2}/{3}",
                                ack.command,
                                ack.result,
                                ack.target_system,
                                ack.target_component));
                        }));
                    }
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)(() => AppendLog("RX parse error: " + ex.Message)));
                }
            }

            private bool EnsureLinkOpen()
            {
                SaveSettings();
                if (MainV2.comPort == null || MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                {
                    CustomMessageBox.Show("No MAVLink connection is open.");
                    return false;
                }

                return true;
            }

            private void SendPing()
            {
                try
                {
                    if (!EnsureLinkOpen())
                    {
                        return;
                    }

                    _pingSequence++;
#pragma warning disable 0612
                    var ping = new MAVLink.mavlink_ping_t
                    {
                        seq = _pingSequence,
                        time_usec = (ulong)DateTime.UtcNow.Ticks,
                        target_system = (byte)_targetSystem.Value,
                        target_component = (byte)_targetComponent.Value,
                    };
#pragma warning restore 0612

                    MainV2.comPort.sendPacket(ping, (int)_targetSystem.Value, (int)_targetComponent.Value);
                    AppendLog(string.Format(CultureInfo.InvariantCulture,
                        "SEND PING seq={0} target={1}/{2}",
                        ping.seq,
                        ping.target_system,
                        ping.target_component));
                }
                catch (Exception ex)
                {
                    AppendLog("SEND PING error: " + ex.Message);
                }
            }

            private void SendBroadcastPing()
            {
                try
                {
                    if (!EnsureLinkOpen())
                    {
                        return;
                    }

                    _pingSequence++;
#pragma warning disable 0612
                    var ping = new MAVLink.mavlink_ping_t
                    {
                        seq = _pingSequence,
                        time_usec = (ulong)DateTime.UtcNow.Ticks,
                        target_system = 0,
                        target_component = 0,
                    };
#pragma warning restore 0612

                    MainV2.comPort.sendPacket(ping, 0, 0);
                    AppendLog(string.Format(CultureInfo.InvariantCulture,
                        "SEND BROADCAST PING seq={0} target=0/0",
                        ping.seq));
                }
                catch (Exception ex)
                {
                    AppendLog("SEND BROADCAST PING error: " + ex.Message);
                }
            }

            private void SendAutopilotVersionRequest()
            {
                try
                {
                    if (!EnsureLinkOpen())
                    {
                        return;
                    }

                    SendCommandLong((ushort)RequestMessageCommandId,
                                    AutopilotVersionMessageId,
                                    0f,
                                    0f,
                                    0f,
                                    0f,
                                    0f,
                                    0f);
                    SetPendingAck((ushort)RequestMessageCommandId, "REQUEST_MESSAGE");
                    AppendLog(string.Format(CultureInfo.InvariantCulture,
                        "SEND REQUEST_MESSAGE target={0}/{1} msgid={2}",
                        _currentTargetSystem,
                        _currentTargetComponent,
                        AutopilotVersionMessageId));
                }
                catch (Exception ex)
                {
                    AppendLog("SEND REQUEST_MESSAGE error: " + ex.Message);
                }
            }

            private void SendVtxCommand()
            {
                try
                {
                    if (!EnsureLinkOpen())
                    {
                        return;
                    }

                    SaveSettings();
                    SendCommandLong((ushort)Esp32VtxCommandId,
                                    (float)_nodeId.Value,
                                    (float)_deviceId.Value,
                                    (float)_band.Value,
                                    (float)_channel.Value,
                                    (float)_powerIndex.Value,
                                    (float)_flags.Value,
                                    0f);
                    SetPendingAck((ushort)Esp32VtxCommandId, "VTX 31001");
                    AppendLog(string.Format(CultureInfo.InvariantCulture,
                        "SEND VTX 31001 target={0}/{1} node={2} device={3} band={4} channel={5} power={6} flags={7}",
                        _currentTargetSystem,
                        _currentTargetComponent,
                        (int)_nodeId.Value,
                        (int)_deviceId.Value,
                        (int)_band.Value,
                        (int)_channel.Value,
                        (int)_powerIndex.Value,
                        (int)_flags.Value));
                }
                catch (Exception ex)
                {
                    AppendLog("SEND VTX 31001 error: " + ex.Message);
                }
            }

            private void SendCommandLong(ushort command,
                                         float param1,
                                         float param2,
                                         float param3,
                                         float param4,
                                         float param5,
                                         float param6,
                                         float param7)
            {
                var request = new MAVLink.mavlink_command_long_t
                {
                    target_system = _currentTargetSystem,
                    target_component = _currentTargetComponent,
                    command = command,
                    confirmation = 0,
                    param1 = param1,
                    param2 = param2,
                    param3 = param3,
                    param4 = param4,
                    param5 = param5,
                    param6 = param6,
                    param7 = param7,
                };

                MainV2.comPort.sendPacket(request, _currentTargetSystem, _currentTargetComponent);
            }

            private void SaveSettings()
            {
                Settings.Instance["esp32route_targetsys"] = _targetSystem.Value.ToString(CultureInfo.InvariantCulture);
                Settings.Instance["esp32route_targetcomp"] = _targetComponent.Value.ToString(CultureInfo.InvariantCulture);
                Settings.Instance["esp32vtx_nodeid"] = _nodeId.Value.ToString(CultureInfo.InvariantCulture);
                Settings.Instance["esp32vtx_deviceid"] = _deviceId.Value.ToString(CultureInfo.InvariantCulture);
                Settings.Instance["esp32vtx_band"] = _band.Value.ToString(CultureInfo.InvariantCulture);
                Settings.Instance["esp32vtx_channel"] = _channel.Value.ToString(CultureInfo.InvariantCulture);
                Settings.Instance["esp32vtx_powerindex"] = _powerIndex.Value.ToString(CultureInfo.InvariantCulture);
                Settings.Instance["esp32vtx_flags"] = _flags.Value.ToString(CultureInfo.InvariantCulture);
            }

            private void SetPendingAck(ushort commandId, string description)
            {
                _pendingAckCommand = commandId;
                _pendingAckDescription = description;
                _commandStatus.Text = string.Format(CultureInfo.InvariantCulture,
                    "Pending ACK: {0}",
                    description);
            }

            private void ClearPendingAck(string status)
            {
                _pendingAckCommand = null;
                _pendingAckDescription = string.Empty;
                _commandStatus.Text = status;
            }

            private static string DescribeMavResult(byte result)
            {
                switch (result)
                {
                    case 0:
                        return "accepted";
                    case 1:
                        return "temporarily rejected";
                    case 2:
                        return "denied";
                    case 3:
                        return "unsupported";
                    case 4:
                        return "failed";
                    case 5:
                        return "in progress";
                    case 6:
                        return "cancelled";
                    default:
                        return "result=" + result.ToString(CultureInfo.InvariantCulture);
                }
            }

            private void AppendLog(string message)
            {
                _log.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
            }

            private void OnFormClosing(object sender, FormClosingEventArgs e)
            {
                SaveSettings();
                DetachPacketHandler();
                e.Cancel = true;
                Hide();
            }
        }
    }
}
