using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner;
using MissionPlanner.Controls;
using MissionPlanner.GCSViews;
using MissionPlanner.Utilities;
using Newtonsoft.Json;

namespace MissionPlanner.plugins
{
    public class Esp32VtxControlPlugin : MissionPlanner.Plugin.Plugin
    {
        private const string FlightDataTabName = "tabEsp32VtxControl";

        private ToolStripMenuItem _menuRoot;
        private ToolStripMenuItem _focusTabMenuItem;
        private TabPage _tabPage;
        private Panel _tabHostPanel;
        private Esp32VtxControlView _view;
        private VtxPluginStateStore _stateStore;

        public override string Name
        {
            get { return "ESP32 VTX Control"; }
        }

        public override string Version
        {
            get { return "0.2"; }
        }

        public override string Author
        {
            get { return "Dionice"; }
        }

        public override bool Init()
        {
            try
            {
                _stateStore = new VtxPluginStateStore();
                loopratehz = 1;
                Log("Init succeeded.");
                return true;
            }
            catch (Exception ex)
            {
                Log("Init failed: " + ex);
                return false;
            }
        }

        public override bool Loaded()
        {
            try
            {
                Log("Loaded start.");
                EnsureView();
                EnsureMenuRegistered();
                EnsureUiHosted();
                ApplySavedHostMode();
                Log("Loaded complete.");
            }
            catch (Exception ex)
            {
                Log("Loaded failed: " + ex);
            }

            return true;
        }

        public override bool Exit()
        {
            try
            {
                if (_view != null)
                {
                    _view.SaveState();
                    _view.DisposeMapQuickControls();
                }
            }
            catch
            {
            }

            try
            {
                if (_focusTabMenuItem != null)
                {
                    _focusTabMenuItem.Click -= FocusTabMenuItemOnClick;
                }

                if (_menuRoot != null)
                {
                    MainV2.instance.MainMenu.Items.Remove(_menuRoot);
                }
            }
            catch
            {
            }

            try
            {
                if (_tabPage != null)
                {
                    if (Host != null && Host.MainForm != null && Host.MainForm.FlightData != null)
                    {
                        try
                        {
                            Host.MainForm.FlightData.TabListOriginal.Remove(_tabPage);
                        }
                        catch
                        {
                        }

                        try
                        {
                            var tabControl = Host.MainForm.FlightData.tabControlactions;
                            if (tabControl.TabPages.Contains(_tabPage))
                            {
                                tabControl.TabPages.Remove(_tabPage);
                            }
                        }
                        catch
                        {
                        }
                    }

                    _tabPage.Dispose();
                    _tabPage = null;
                    _tabHostPanel = null;
                }
            }
            catch
            {
            }

            return true;
        }

        public override bool Loop()
        {
            try
            {
                EnsureMenuRegistered();
                EnsureUiHosted();
                if (_view != null)
                {
                    _view.EnsureMapQuickControlsHosted();
                }


            }
            catch (Exception ex)
            {
                Log("Loop failed: " + ex);
            }

            return true;
        }

        private void EnsureView()
        {
            if (_view != null)
            {
                return;
            }

            _view = new Esp32VtxControlView(_stateStore);
            _view.Dock = DockStyle.Fill;
            Log("View created.");
        }

        private void EnsureMenuRegistered()
        {
            if (MainV2.instance == null || MainV2.instance.MainMenu == null)
            {
                return;
            }

            if (MainV2.instance.InvokeRequired)
            {
                try
                {
                    MainV2.instance.BeginInvoke(new Action(EnsureMenuRegistered));
                }
                catch
                {
                }

                return;
            }

            if (_menuRoot == null)
            {
                _menuRoot = new ToolStripMenuItem("ESP32 VTX Control");

                _focusTabMenuItem = new ToolStripMenuItem("Open In Flight Data");
                _focusTabMenuItem.Click += FocusTabMenuItemOnClick;
                _menuRoot.DropDownItems.Add(_focusTabMenuItem);
            }

            if (!MainV2.instance.MainMenu.Items.Contains(_menuRoot))
            {
                MainV2.instance.MainMenu.Items.Add(_menuRoot);
                Log("Menu registered.");
            }
        }

        private void EnsureUiHosted()
        {
            if (Host == null || Host.MainForm == null || Host.MainForm.FlightData == null)
            {
                return;
            }

            Action ensureAction = () =>
            {
                try
                {
                    EnsureTabHost();

                    if (_tabPage == null || _tabHostPanel == null)
                    {
                        return;
                    }

                    var tabControl = Host.MainForm.FlightData.tabControlactions;
                    if (!tabControl.TabPages.Contains(_tabPage))
                    {
                        tabControl.TabPages.Insert(Math.Min(5, tabControl.TabPages.Count), _tabPage);
                    }

                    if (!Host.MainForm.FlightData.TabListOriginal.Contains(_tabPage))
                    {
                        Host.MainForm.FlightData.TabListOriginal.Add(_tabPage);
                    }

                    if (_view != null && _view.Parent != _tabHostPanel)
                    {
                        AttachViewToTab();
                    }
                }
                catch
                {
                }
            };

            try
            {
                if (FlightData.instance != null && FlightData.instance.InvokeRequired)
                {
                    FlightData.instance.BeginInvoke(ensureAction);
                }
                else
                {
                    ensureAction();
                }
            }
            catch
            {
            }
        }

        private void EnsureTabHost()
        {
            if (_tabPage != null || Host == null || Host.MainForm == null || Host.MainForm.FlightData == null)
            {
                return;
            }

            EnsureView();

            _tabPage = new TabPage();
            _tabPage.Text = "ESP32 VTX";
            _tabPage.Name = FlightDataTabName;

            _tabHostPanel = new Panel();
            _tabHostPanel.Dock = DockStyle.Fill;
            _tabPage.Controls.Add(_tabHostPanel);

            Host.MainForm.FlightData.TabListOriginal.Add(_tabPage);
            var tabControl = Host.MainForm.FlightData.tabControlactions;
            if (!tabControl.TabPages.Contains(_tabPage))
            {
                tabControl.TabPages.Insert(Math.Min(5, tabControl.TabPages.Count), _tabPage);
            }

            ThemeManager.ApplyThemeTo(_tabPage);
            AttachViewToTab();
            Log("Tab host created.");
        }

        private void ApplySavedHostMode()
        {
            EnsureView();
            AttachViewToTab();
        }

        private void FocusTabMenuItemOnClick(object sender, EventArgs e)
        {
            AttachViewToTab();
            FocusTab();
        }

        private void AttachViewToTab()
        {
            EnsureView();
            EnsureTabHost();
            if (_tabHostPanel == null)
            {
                return;
            }

            MoveViewTo(_tabHostPanel);
        }

        private void FocusTab()
        {
            try
            {
                if (_tabPage != null && Host != null && Host.MainForm != null && Host.MainForm.FlightData != null)
                {
                    Host.MainForm.FlightData.tabControlactions.SelectedTab = _tabPage;
                }
            }
            catch
            {
            }
        }

        private void MoveViewTo(Control hostControl)
        {
            if (hostControl == null || _view == null)
            {
                return;
            }

            if (_view.Parent != null)
            {
                _view.Parent.Controls.Remove(_view);
            }

            hostControl.Controls.Add(_view);
            _view.Dock = DockStyle.Fill;
        }

        private static void Log(string message)
        {
            try
            {
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine;
                File.AppendAllText(Path.Combine(Settings.GetDataDirectory(), "esp32-vtx-control-startup.log"), line);
            }
            catch
            {
            }

            try
            {
                Trace.WriteLine("Esp32VtxControlPlugin: " + message);
            }
            catch
            {
            }
        }
    }

    internal class Esp32VtxControlView : UserControl
    {
        private const ushort Esp32VtxCommandId = 31001;
        private const byte DefaultTargetSystemId = 1;
        private const byte DefaultTargetComponentId = 68;
        private const byte DefaultNodeId = 1;
        private const byte DefaultDeviceId = 1;

        private readonly VtxPluginStateStore _stateStore;
        private readonly ListBox _instanceList = new ListBox();
        private readonly TextBox _instanceName = new TextBox();
        private readonly NumericUpDown _targetSystem = new NumericUpDown();
        private readonly NumericUpDown _targetComponent = new NumericUpDown();
        private readonly NumericUpDown _nodeId = new NumericUpDown();
        private readonly NumericUpDown _deviceId = new NumericUpDown();
        private readonly NumericUpDown _flags = new NumericUpDown();
        private readonly ComboBox _tableSelector = new ComboBox();
        private readonly ComboBox _bandSelector = new ComboBox();
        private readonly ComboBox _channelSelector = new ComboBox();
        private readonly ComboBox _powerSelector = new ComboBox();
        private readonly Label _instanceSummary = new Label();
        private readonly Label _statusLabel = new Label();
        private readonly TextBox _log = new TextBox();
        private FlowLayoutPanel _mapQuickPanel;
        private Control _mapQuickParent;
        private string _mapQuickLayoutKey;
        private bool _loading;

        public Esp32VtxControlView(VtxPluginStateStore stateStore)
        {
            _stateStore = stateStore;
            EnsureDefaultState();
            InitializeComponent();
            LoadFromState();
        }

        public void SaveState()
        {
            SaveCurrentInstance();
            _stateStore.Save();
        }

        public void EnsureMapQuickControlsHosted()
        {
            if (FlightData.instance == null || FlightData.instance.gMapControl1 == null)
            {
                return;
            }

            if (FlightData.instance.InvokeRequired)
            {
                try
                {
                    FlightData.instance.BeginInvoke(new Action(EnsureMapQuickControlsHosted));
                }
                catch
                {
                }

                return;
            }

            var parent = FlightData.instance.gMapControl1.Parent;
            if (parent == null || parent.IsDisposed)
            {
                return;
            }

            if (_mapQuickPanel == null || _mapQuickPanel.IsDisposed || _mapQuickParent != parent)
            {
                DisposeMapQuickControls();

                _mapQuickParent = parent;
                _mapQuickPanel = new FlowLayoutPanel();
                _mapQuickPanel.AutoSize = true;
                _mapQuickPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                _mapQuickPanel.FlowDirection = FlowDirection.TopDown;
                _mapQuickPanel.WrapContents = false;
                _mapQuickPanel.BackColor = Color.Transparent;
                _mapQuickPanel.Padding = new Padding(8, 6, 8, 6);
                _mapQuickPanel.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

                parent.Controls.Add(_mapQuickPanel);
                parent.Resize += MapQuickParentOnResize;
                _mapQuickLayoutKey = null;
            }

            var layoutKey = BuildMapQuickLayoutKey();
            if (!string.Equals(_mapQuickLayoutKey, layoutKey, StringComparison.Ordinal))
            {
                RebuildMapQuickControls(layoutKey);
            }
            else if (_mapQuickPanel.Visible)
            {
                PositionMapQuickPanel();
                _mapQuickPanel.BringToFront();
            }
        }

        public void DisposeMapQuickControls()
        {
            try
            {
                if (_mapQuickParent != null)
                {
                    _mapQuickParent.Resize -= MapQuickParentOnResize;
                }
            }
            catch
            {
            }

            try
            {
                if (_mapQuickPanel != null)
                {
                    if (_mapQuickPanel.Parent != null)
                    {
                        _mapQuickPanel.Parent.Controls.Remove(_mapQuickPanel);
                    }

                    _mapQuickPanel.Dispose();
                }
            }
            catch
            {
            }

            _mapQuickPanel = null;
            _mapQuickParent = null;
            _mapQuickLayoutKey = null;
        }

        private void EnsureDefaultState()
        {
            if (_stateStore.State.Tables == null)
            {
                _stateStore.State.Tables = new List<VtxTableLibraryEntry>();
            }

            if (_stateStore.State.Instances == null)
            {
                _stateStore.State.Instances = new List<VtxControlInstanceState>();
            }

            if (_stateStore.State.Layout == null)
            {
                _stateStore.State.Layout = new VtxPluginLayoutState();
            }

            if (_stateStore.State.Instances.Count == 0)
            {
                var instance = BuildDefaultInstance();
                _stateStore.State.Instances.Add(instance);
                _stateStore.State.SelectedInstanceId = instance.Id;
                _stateStore.Save();
            }
        }

        private void InitializeComponent()
        {
            Dock = DockStyle.Fill;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            Controls.Add(root);

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.FixedPanel = FixedPanel.Panel1;
            split.Panel1MinSize = 90;
            root.Controls.Add(split, 0, 0);

            split.Panel1.Controls.Add(BuildInstancesPane());
            split.Panel2.Controls.Add(BuildEditorPane());
            split.SizeChanged += delegate
            {
                try
                {
                    if (split.Width <= 300)
                    {
                        return;
                    }

                    var maxDistance = split.Width - 220;
                    if (maxDistance < split.Panel1MinSize)
                    {
                        return;
                    }

                    var distance = Math.Max(split.Panel1MinSize, Math.Min(100, maxDistance));
                    if (split.SplitterDistance != distance)
                    {
                        split.SplitterDistance = distance;
                    }
                }
                catch
                {
                }
            };

            var bottomPanel = new TableLayoutPanel();
            bottomPanel.Dock = DockStyle.Fill;
            bottomPanel.ColumnCount = 1;
            bottomPanel.RowCount = 2;
            bottomPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottomPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _statusLabel.AutoSize = true;
            _statusLabel.ForeColor = Color.White;
            _statusLabel.Padding = new Padding(6, 6, 6, 6);
            _statusLabel.Text = "Ready.";
            bottomPanel.Controls.Add(_statusLabel, 0, 0);

            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            bottomPanel.Controls.Add(_log, 0, 1);
            root.Controls.Add(bottomPanel, 0, 1);
        }

        private Control BuildInstancesPane()
        {
            var group = new GroupBox();
            group.Text = "Instances";
            group.Dock = DockStyle.Fill;
            group.ForeColor = Color.White;

            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.ColumnCount = 1;
            panel.RowCount = 2;
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Padding = new Padding(6, 10, 6, 6);

            _instanceList.Dock = DockStyle.Fill;
            _instanceList.DisplayMember = "DisplayName";
            _instanceList.IntegralHeight = false;
            _instanceList.Margin = new Padding(0, 0, 0, 6);
            _instanceList.SelectedIndexChanged += InstanceListOnSelectedIndexChanged;
            panel.Controls.Add(_instanceList, 0, 0);

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.AutoSize = true;
            buttons.WrapContents = true;
            buttons.Margin = new Padding(0);

            var addButton = new Button();
            addButton.Text = "Add";
            addButton.AutoSize = true;
            addButton.Click += AddButtonOnClick;

            var removeButton = new Button();
            removeButton.Text = "Remove";
            removeButton.AutoSize = true;
            removeButton.Click += RemoveButtonOnClick;

            var settingsButton = new Button();
            settingsButton.Text = "Settings";
            settingsButton.AutoSize = true;
            settingsButton.Click += OpenSettingsButtonOnClick;

            buttons.Controls.Add(addButton);
            buttons.Controls.Add(removeButton);
            buttons.Controls.Add(settingsButton);
            panel.Controls.Add(buttons, 0, 1);

            group.Controls.Add(panel);
            return group;
        }

        private Control BuildEditorPane()
        {
            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.ColumnCount = 1;
            panel.RowCount = 4;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.Padding = new Padding(8, 4, 8, 8);

            panel.Controls.Add(BuildInstanceSummaryRow(), 0, 0);
            panel.Controls.Add(BuildControlGroup(), 0, 1);
            panel.Controls.Add(BuildActionRow(), 0, 2);
            panel.Controls.Add(BuildHelpBox(), 0, 3);

            return panel;
        }

        private Control BuildInstanceSummaryRow()
        {
            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Top;
            panel.AutoSize = true;
            panel.ColumnCount = 2;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.Margin = new Padding(0, 0, 0, 6);

            _instanceSummary.AutoSize = true;
            _instanceSummary.ForeColor = Color.White;
            _instanceSummary.Margin = new Padding(0, 6, 12, 0);
            _instanceSummary.Text = "No instance selected.";
            panel.Controls.Add(_instanceSummary, 0, 0);

            var settingsButton = new Button();
            settingsButton.Text = "Instance Settings";
            settingsButton.AutoSize = true;
            settingsButton.Click += OpenSettingsButtonOnClick;
            panel.Controls.Add(settingsButton, 1, 0);

            return panel;
        }

        private Control BuildControlGroup()
        {
            var group = new GroupBox();
            group.Text = "VTX Control";
            group.Dock = DockStyle.Top;
            group.AutoSize = true;
            group.Margin = new Padding(0, 0, 0, 6);
            group.ForeColor = Color.White;

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.Padding = new Padding(8, 6, 8, 6);
            layout.AutoSize = true;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            _bandSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            _bandSelector.SelectedIndexChanged += BandSelectorOnSelectedIndexChanged;
            ConfigureCompactDropDown(_bandSelector, 110);

            _channelSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            _channelSelector.SelectedIndexChanged += AnyEditorChanged;
            ConfigureCompactDropDown(_channelSelector, 130);

            _powerSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            _powerSelector.SelectedIndexChanged += AnyEditorChanged;
            ConfigureCompactDropDown(_powerSelector, 75);

            layout.Controls.Add(BuildCompactFieldRow("Band", _bandSelector), 0, 0);
            layout.Controls.Add(BuildCompactFieldRow("Channel", _channelSelector), 0, 1);
            layout.Controls.Add(BuildCompactFieldRow("Power", _powerSelector), 0, 2);

            group.Controls.Add(layout);
            return group;
        }

        private Control BuildActionRow()
        {
            var panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Top;
            panel.AutoSize = true;
            panel.WrapContents = true;
            panel.Margin = new Padding(0, 0, 0, 6);

            var sendButton = new Button();
            sendButton.Text = "Send VTX Command";
            sendButton.AutoSize = true;
            sendButton.Click += SendButtonOnClick;

            panel.Controls.Add(sendButton);
            return panel;
        }

        private Control BuildHelpBox()
        {
            var label = new Label();
            label.Dock = DockStyle.Fill;
            label.AutoSize = true;
            label.MaximumSize = new Size(460, 0);
            label.Padding = new Padding(2, 0, 2, 0);
            label.ForeColor = Color.White;
            label.Text = "Use Instance Settings for routing and table selection. On the main panel, choose band, channel, and power for the selected instance, then send the command.";
            return label;
        }

        private void LoadFromState()
        {
            _loading = true;
            try
            {
                RefreshInstanceList();
                SelectInstanceById(_stateStore.State.SelectedInstanceId);
            }
            finally
            {
                _loading = false;
            }

            EnsureMapQuickControlsHosted();
        }

        private void RefreshInstanceList()
        {
            var selectedId = CurrentInstance != null ? CurrentInstance.Id : _stateStore.State.SelectedInstanceId;

            _instanceList.BeginUpdate();
            try
            {
                _instanceList.Items.Clear();
                foreach (var instance in _stateStore.State.Instances)
                {
                    _instanceList.Items.Add(new InstanceListItem(instance));
                }
            }
            finally
            {
                _instanceList.EndUpdate();
            }

            SelectInstanceById(selectedId);
        }

        private void RefreshTableSelector()
        {
            var selectedTableId = CurrentInstance != null ? CurrentInstance.TableId : null;

            _tableSelector.BeginUpdate();
            try
            {
                _tableSelector.Items.Clear();
                foreach (var table in _stateStore.State.Tables)
                {
                    _tableSelector.Items.Add(new TableListItem(table));
                }
            }
            finally
            {
                _tableSelector.EndUpdate();
            }

            SelectTableById(selectedTableId);
        }

        private void SelectInstanceById(string instanceId)
        {
            if (_instanceList.Items.Count == 0)
            {
                return;
            }

            for (var index = 0; index < _instanceList.Items.Count; index++)
            {
                var item = _instanceList.Items[index] as InstanceListItem;
                if (item != null && item.State.Id == instanceId)
                {
                    _instanceList.SelectedIndex = index;
                    return;
                }
            }

            if (_instanceList.Items.Count > 0)
            {
                _instanceList.SelectedIndex = 0;
            }
        }

        private void SelectTableById(string tableId)
        {
            if (string.IsNullOrWhiteSpace(tableId))
            {
                _tableSelector.SelectedIndex = -1;
                RefreshBandSelector(null);
                return;
            }

            for (var index = 0; index < _tableSelector.Items.Count; index++)
            {
                var item = _tableSelector.Items[index] as TableListItem;
                if (item != null && item.Entry.Id == tableId)
                {
                    _tableSelector.SelectedIndex = index;
                    return;
                }
            }

            _tableSelector.SelectedIndex = -1;
            RefreshBandSelector(null);
        }

        private void LoadInstanceIntoEditor(VtxControlInstanceState instance)
        {
            _loading = true;
            try
            {
                RefreshInstanceSummary(instance);
                RefreshBandSelector(FindTableById(instance.TableId));
                SelectBand(instance.SelectedBandIndex);
                SelectChannel(instance.SelectedChannelIndex);
                SelectPower(instance.SelectedPowerValue);
            }
            finally
            {
                _loading = false;
            }
        }

        private void SaveCurrentInstance()
        {
            if (_loading)
            {
                return;
            }

            var instance = CurrentInstance;
            if (instance == null)
            {
                return;
            }

            instance.SelectedBandIndex = _bandSelector.SelectedItem is BandItem
                ? ((BandItem)_bandSelector.SelectedItem).Index
                : 0;
            instance.SelectedChannelIndex = _channelSelector.SelectedItem is ChannelItem
                ? ((ChannelItem)_channelSelector.SelectedItem).Index
                : 0;
            instance.SelectedPowerValue = _powerSelector.SelectedItem is PowerItem
                ? ((PowerItem)_powerSelector.SelectedItem).Index
                : 0;

            _stateStore.State.SelectedInstanceId = instance.Id;
            _stateStore.Save();
            RefreshInstanceList();
            EnsureMapQuickControlsHosted();
        }

        private VtxControlInstanceState CurrentInstance
        {
            get
            {
                var item = _instanceList.SelectedItem as InstanceListItem;
                return item != null ? item.State : null;
            }
        }

        private VtxTableLibraryEntry SelectedTable
        {
            get
            {
                return FindTableById(CurrentInstance != null ? CurrentInstance.TableId : null);
            }
        }

        private void AddButtonOnClick(object sender, EventArgs e)
        {
            SaveCurrentInstance();
            var instance = BuildDefaultInstance();
            if (_stateStore.State.Tables.Count > 0)
            {
                instance.TableId = _stateStore.State.Tables[0].Id;
            }

            _stateStore.State.Instances.Add(instance);
            _stateStore.State.SelectedInstanceId = instance.Id;
            _stateStore.Save();
            RefreshInstanceList();
            SelectInstanceById(instance.Id);
            OpenInstanceSettingsDialog(instance);
            AppendLog("Added instance " + instance.Name + ".");
            EnsureMapQuickControlsHosted();
        }

        private void RemoveButtonOnClick(object sender, EventArgs e)
        {
            var instance = CurrentInstance;
            if (instance == null)
            {
                return;
            }

            if (_stateStore.State.Instances.Count <= 1)
            {
                MessageBox.Show("At least one VTX instance must remain.", "ESP32 VTX Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _stateStore.State.Instances.Remove(instance);
            _stateStore.State.SelectedInstanceId = _stateStore.State.Instances[0].Id;
            _stateStore.Save();
            RefreshInstanceList();
            AppendLog("Removed instance " + instance.Name + ".");
            EnsureMapQuickControlsHosted();
        }

        private void InstanceListOnSelectedIndexChanged(object sender, EventArgs e)
        {
            var instance = CurrentInstance;
            if (instance == null)
            {
                return;
            }

            _stateStore.State.SelectedInstanceId = instance.Id;
            _stateStore.Save();
            LoadInstanceIntoEditor(instance);
        }

        private void BandSelectorOnSelectedIndexChanged(object sender, EventArgs e)
        {
            RefreshChannelSelector();
            AnyEditorChanged(sender, e);
        }

        private void AnyEditorChanged(object sender, EventArgs e)
        {
            SaveCurrentInstance();
        }

        private void RefreshBandSelector(VtxTableLibraryEntry table)
        {
            var instance = CurrentInstance;
            var selectedBandIndex = instance != null ? instance.SelectedBandIndex : 0;

            _bandSelector.BeginUpdate();
            try
            {
                _bandSelector.Items.Clear();
                if (table != null && table.Table != null && table.Table.VtxTable != null && table.Table.VtxTable.BandsList != null)
                {
                    for (var index = 0; index < table.Table.VtxTable.BandsList.Count; index++)
                    {
                        var band = table.Table.VtxTable.BandsList[index];
                        _bandSelector.Items.Add(new BandItem(index, band));
                    }
                }
            }
            finally
            {
                _bandSelector.EndUpdate();
            }

            if (_bandSelector.Items.Count > 0)
            {
                _bandSelector.SelectedIndex = Math.Max(0, Math.Min(selectedBandIndex, _bandSelector.Items.Count - 1));
            }
            else
            {
                _bandSelector.SelectedIndex = -1;
            }

            RefreshChannelSelector();
            RefreshPowerSelector();
        }

        private void RefreshChannelSelector()
        {
            var instance = CurrentInstance;
            var selectedChannelIndex = instance != null ? instance.SelectedChannelIndex : 0;
            var bandItem = _bandSelector.SelectedItem as BandItem;

            _channelSelector.BeginUpdate();
            try
            {
                _channelSelector.Items.Clear();
                if (bandItem != null && bandItem.Band.Frequencies != null)
                {
                    for (var index = 0; index < bandItem.Band.Frequencies.Count; index++)
                    {
                        _channelSelector.Items.Add(new ChannelItem(index, bandItem.Band.Frequencies[index]));
                    }
                }
            }
            finally
            {
                _channelSelector.EndUpdate();
            }

            if (_channelSelector.Items.Count > 0)
            {
                _channelSelector.SelectedIndex = Math.Max(0, Math.Min(selectedChannelIndex, _channelSelector.Items.Count - 1));
            }
            else
            {
                _channelSelector.SelectedIndex = -1;
            }
        }

        private void RefreshPowerSelector()
        {
            var instance = CurrentInstance;
            var selectedPowerValue = instance != null ? instance.SelectedPowerValue : 0;
            var table = SelectedTable;

            _powerSelector.BeginUpdate();
            try
            {
                _powerSelector.Items.Clear();
                if (table != null && table.Table != null && table.Table.VtxTable != null && table.Table.VtxTable.PowerlevelsList != null)
                {
                    for (var index = 0; index < table.Table.VtxTable.PowerlevelsList.Count; index++)
                    {
                        _powerSelector.Items.Add(new PowerItem(index, table.Table.VtxTable.PowerlevelsList[index]));
                    }
                }
            }
            finally
            {
                _powerSelector.EndUpdate();
            }

            SelectPower(selectedPowerValue);
        }

        private void SelectBand(int bandIndex)
        {
            if (_bandSelector.Items.Count == 0)
            {
                _bandSelector.SelectedIndex = -1;
                return;
            }

            _bandSelector.SelectedIndex = Math.Max(0, Math.Min(bandIndex, _bandSelector.Items.Count - 1));
        }

        private void SelectChannel(int channelIndex)
        {
            if (_channelSelector.Items.Count == 0)
            {
                _channelSelector.SelectedIndex = -1;
                return;
            }

            _channelSelector.SelectedIndex = Math.Max(0, Math.Min(channelIndex, _channelSelector.Items.Count - 1));
        }

        private void SelectPower(int powerSelection)
        {
            if (_powerSelector.Items.Count == 0)
            {
                _powerSelector.SelectedIndex = -1;
                return;
            }

            for (var index = 0; index < _powerSelector.Items.Count; index++)
            {
                var item = _powerSelector.Items[index] as PowerItem;
                if (item != null && (item.Index == powerSelection || item.Value == powerSelection))
                {
                    _powerSelector.SelectedIndex = index;
                    return;
                }
            }

            _powerSelector.SelectedIndex = 0;
        }

        private void ImportButtonOnClick(object sender, EventArgs e)
        {
            ImportTableAndSelect(null);
        }

        private void RemoveTableButtonOnClick(object sender, EventArgs e)
        {
            RemoveSelectedTable(null);
        }

        private void OpenSettingsButtonOnClick(object sender, EventArgs e)
        {
            OpenInstanceSettingsDialog(CurrentInstance);
        }

        private void OpenInstanceSettingsDialog(VtxControlInstanceState instance)
        {
            if (instance == null)
            {
                return;
            }

            using (var dialog = new Form())
            {
                dialog.Text = "Instance Settings";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.AutoSize = true;
                dialog.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                dialog.Padding = new Padding(10);

                var nameBox = new TextBox();
                ConfigureCompactText(nameBox, 220);
                nameBox.Text = instance.Name ?? string.Empty;

                var targetSystem = CreateDialogNumeric(0, 255, instance.TargetSystem);
                var targetComponent = CreateDialogNumeric(0, 255, instance.TargetComponent);
                var nodeId = CreateDialogNumeric(1, 255, instance.VtxNodeId);
                var deviceId = CreateDialogNumeric(1, 255, instance.VtxDeviceId);
                var flags = CreateDialogNumeric(0, 255, instance.Flags);
                var showOnMap = new CheckBox();
                showOnMap.Text = "Show quick power buttons on map";
                showOnMap.AutoSize = true;
                showOnMap.Checked = instance.ShowOnMap;

                var tableSelector = new ComboBox();
                tableSelector.DropDownStyle = ComboBoxStyle.DropDownList;
                ConfigureCompactDropDown(tableSelector, 240);
                PopulateTableSelector(tableSelector, instance.TableId);

                var body = new TableLayoutPanel();
                body.Dock = DockStyle.Fill;
                body.AutoSize = true;
                body.ColumnCount = 1;
                body.RowCount = 9;
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

                body.Controls.Add(BuildCompactFieldRow("Name", nameBox), 0, 0);
                body.Controls.Add(BuildCompactPairRow("Target Sys", targetSystem, "Comp", targetComponent), 0, 1);
                body.Controls.Add(BuildCompactPairRow("VTX Node", nodeId, "Device", deviceId), 0, 2);
                body.Controls.Add(BuildCompactFieldRow("Flags", flags), 0, 3);
                body.Controls.Add(BuildCompactFieldRow("Map", showOnMap), 0, 4);
                body.Controls.Add(BuildCompactFieldRow("Table", tableSelector), 0, 5);

                var libraryButtons = new FlowLayoutPanel();
                libraryButtons.Dock = DockStyle.Top;
                libraryButtons.AutoSize = true;
                libraryButtons.WrapContents = false;
                libraryButtons.Margin = new Padding(0, 2, 0, 2);

                var importButton = new Button();
                importButton.Text = "Import JSON";
                importButton.AutoSize = true;
                importButton.Click += delegate { ImportTableAndSelect(tableSelector); };

                var removeButton = new Button();
                removeButton.Text = "Remove Table";
                removeButton.AutoSize = true;
                removeButton.Click += delegate { RemoveSelectedTable(tableSelector); };

                libraryButtons.Controls.Add(importButton);
                libraryButtons.Controls.Add(removeButton);
                body.Controls.Add(BuildCompactFieldRow("Library", libraryButtons), 0, 6);

                var buttons = new FlowLayoutPanel();
                buttons.Dock = DockStyle.Top;
                buttons.AutoSize = true;
                buttons.FlowDirection = FlowDirection.RightToLeft;

                var saveButton = new Button();
                saveButton.Text = "Save";
                saveButton.AutoSize = true;
                saveButton.Click += delegate
                {
                    instance.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "VTX Instance" : nameBox.Text.Trim();
                    instance.TargetSystem = (byte)targetSystem.Value;
                    instance.TargetComponent = (byte)targetComponent.Value;
                    instance.VtxNodeId = (byte)nodeId.Value;
                    instance.VtxDeviceId = (byte)deviceId.Value;
                    instance.Flags = (byte)flags.Value;
                    instance.ShowOnMap = showOnMap.Checked;
                    instance.TableId = tableSelector.SelectedItem is TableListItem ? ((TableListItem)tableSelector.SelectedItem).Entry.Id : null;

                    _stateStore.Save();
                    RefreshInstanceList();
                    LoadInstanceIntoEditor(instance);
                    EnsureMapQuickControlsHosted();
                    SetStatus("Saved settings for " + instance.Name + ".");
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };

                var cancelButton = new Button();
                cancelButton.Text = "Cancel";
                cancelButton.AutoSize = true;
                cancelButton.Click += delegate
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                };

                buttons.Controls.Add(saveButton);
                buttons.Controls.Add(cancelButton);
                body.Controls.Add(buttons, 0, 7);

                dialog.Controls.Add(body);
                dialog.ShowDialog(this);
            }
        }

        private void ImportTableAndSelect(ComboBox tableSelector)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                dialog.Title = "Import VTX Table JSON";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var table = JsonConvert.DeserializeObject<VtxTableDocument>(json);
                    ValidateTable(table, dialog.FileName);

                    var existing = _stateStore.State.Tables.FirstOrDefault(item => string.Equals(item.SourcePath, dialog.FileName, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        existing = new VtxTableLibraryEntry();
                        existing.Id = Guid.NewGuid().ToString("N");
                        _stateStore.State.Tables.Add(existing);
                    }

                    existing.SourcePath = dialog.FileName;
                    existing.Name = BuildTableName(table, dialog.FileName);
                    existing.Table = table;
                    existing.ImportedUtc = DateTime.UtcNow;

                    var instance = CurrentInstance;
                    if (instance != null)
                    {
                        instance.TableId = existing.Id;
                    }

                    _stateStore.Save();
                    if (tableSelector != null)
                    {
                        PopulateTableSelector(tableSelector, existing.Id);
                    }
                    if (CurrentInstance != null && CurrentInstance.Id == instance.Id)
                    {
                        LoadInstanceIntoEditor(CurrentInstance);
                    }
                    EnsureMapQuickControlsHosted();
                    AppendLog("Imported table " + existing.Name + ".");
                    SetStatus("Imported VTX table: " + existing.Name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to import VTX table: " + ex.Message, "ESP32 VTX Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RemoveSelectedTable(ComboBox tableSelector)
        {
            var table = tableSelector != null
                ? (tableSelector.SelectedItem as TableListItem != null ? ((TableListItem)tableSelector.SelectedItem).Entry : null)
                : SelectedTable;
            if (table == null)
            {
                return;
            }

            if (MessageBox.Show("Remove selected table from the library?", "ESP32 VTX Control", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            _stateStore.State.Tables.Remove(table);
            foreach (var instance in _stateStore.State.Instances)
            {
                if (instance.TableId == table.Id)
                {
                    instance.TableId = null;
                    instance.SelectedBandIndex = 0;
                    instance.SelectedChannelIndex = 0;
                    instance.SelectedPowerValue = 0;
                }
            }

            _stateStore.Save();
            if (tableSelector != null)
            {
                PopulateTableSelector(tableSelector, null);
            }
            LoadInstanceIntoEditor(CurrentInstance ?? _stateStore.State.Instances[0]);
            EnsureMapQuickControlsHosted();
            AppendLog("Removed table " + table.Name + ".");
        }

        private void SendButtonOnClick(object sender, EventArgs e)
        {
            try
            {
                SaveCurrentInstance();
                var instance = CurrentInstance;
                if (instance == null)
                {
                    return;
                }

                if (MainV2.comPort == null || MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                {
                    MessageBox.Show("No MAVLink connection is open.", "ESP32 VTX Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var bandItem = _bandSelector.SelectedItem as BandItem;
                var channelItem = _channelSelector.SelectedItem as ChannelItem;
                var powerItem = _powerSelector.SelectedItem as PowerItem;
                if (bandItem == null || channelItem == null || powerItem == null)
                {
                    MessageBox.Show("Select a table, band, channel, and power before sending.", "ESP32 VTX Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                SendVtxCommand(instance, bandItem, channelItem, powerItem.Index, powerItem.DisplayName);
            }
            catch (Exception ex)
            {
                SetStatus("Send failed: " + ex.Message);
                AppendLog("Send failed: " + ex.Message);
            }
        }

        private void SetStatus(string message)
        {
            _statusLabel.Text = message;
        }

        private void RefreshInstanceSummary(VtxControlInstanceState instance)
        {
            if (instance == null)
            {
                _instanceSummary.Text = "No instance selected.";
                return;
            }

            var tableName = SelectedTable != null ? SelectedTable.Name : "No table";
            _instanceSummary.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} | route {1}/{2} | vtx {3}/{4} | table {5}",
                string.IsNullOrWhiteSpace(instance.Name) ? "VTX Instance" : instance.Name,
                instance.TargetSystem,
                instance.TargetComponent,
                instance.VtxNodeId,
                instance.VtxDeviceId,
                tableName);
        }

        public void AutoSendOnConnect()
        {
            if (_stateStore.State.Instances == null)
            {
                return;
            }

            foreach (var instance in _stateStore.State.Instances)
            {
                TrySendCurrentSelection(instance, "on connect");
            }
        }

        private bool TrySendCurrentSelection(VtxControlInstanceState instance, string reason)
        {
            if (instance == null)
            {
                return false;
            }

            try
            {
                if (MainV2.comPort == null || MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                {
                    return false;
                }

                var table = FindTableById(instance.TableId);
                if (table == null || table.Table == null || table.Table.VtxTable == null)
                {
                    AppendLog("Auto-send skipped (" + reason + "): no table for " + instance.Name + ".");
                    return false;
                }

                if (table.Table.VtxTable.BandsList == null
                    || instance.SelectedBandIndex < 0
                    || instance.SelectedBandIndex >= table.Table.VtxTable.BandsList.Count)
                {
                    AppendLog("Auto-send skipped (" + reason + "): invalid band for " + instance.Name + ".");
                    return false;
                }

                var band = table.Table.VtxTable.BandsList[instance.SelectedBandIndex];
                if (band.Frequencies == null
                    || instance.SelectedChannelIndex < 0
                    || instance.SelectedChannelIndex >= band.Frequencies.Count)
                {
                    AppendLog("Auto-send skipped (" + reason + "): invalid channel for " + instance.Name + ".");
                    return false;
                }

                var powerIndex = instance.SelectedPowerValue;
                var bandItem = new BandItem(instance.SelectedBandIndex, band);
                var channelItem = new ChannelItem(instance.SelectedChannelIndex, band.Frequencies[instance.SelectedChannelIndex]);
                var powerLevel = table.Table.VtxTable.PowerlevelsList != null
                    && powerIndex >= 0
                    && powerIndex < table.Table.VtxTable.PowerlevelsList.Count
                    ? table.Table.VtxTable.PowerlevelsList[powerIndex]
                    : null;
                var powerDisplay = powerLevel != null
                    ? new PowerItem(powerIndex, powerLevel).DisplayName
                    : powerIndex.ToString(CultureInfo.InvariantCulture);

                SendVtxCommand(instance, bandItem, channelItem, powerIndex, powerDisplay);
                AppendLog("Auto-sent (" + reason + "): " + instance.Name
                    + " band=" + bandItem.BandDisplay
                    + " ch=" + channelItem.DisplayName
                    + " power=" + powerDisplay + ".");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("Auto-send failed (" + reason + ") for " + (instance.Name ?? "?") + ": " + ex.Message);
                return false;
            }
        }

        private void SendQuickPower(VtxControlInstanceState instance, int powerIndex)
        {
            if (instance == null)
            {
                return;
            }

            try
            {
                if (MainV2.comPort == null || MainV2.comPort.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                {
                    MessageBox.Show("No MAVLink connection is open.", "ESP32 VTX Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var table = FindTableById(instance.TableId);
                if (table == null || table.Table == null || table.Table.VtxTable == null)
                {
                    SetStatus("Quick send failed: no table configured for " + instance.Name + ".");
                    return;
                }

                if (table.Table.VtxTable.BandsList == null || instance.SelectedBandIndex < 0 || instance.SelectedBandIndex >= table.Table.VtxTable.BandsList.Count)
                {
                    SetStatus("Quick send failed: invalid band selection for " + instance.Name + ".");
                    return;
                }

                var band = table.Table.VtxTable.BandsList[instance.SelectedBandIndex];
                if (band.Frequencies == null || instance.SelectedChannelIndex < 0 || instance.SelectedChannelIndex >= band.Frequencies.Count)
                {
                    SetStatus("Quick send failed: invalid channel selection for " + instance.Name + ".");
                    return;
                }

                var bandItem = new BandItem(instance.SelectedBandIndex, band);
                var channelItem = new ChannelItem(instance.SelectedChannelIndex, band.Frequencies[instance.SelectedChannelIndex]);
                var powerLevel = table.Table.VtxTable.PowerlevelsList != null && powerIndex >= 0 && powerIndex < table.Table.VtxTable.PowerlevelsList.Count
                    ? table.Table.VtxTable.PowerlevelsList[powerIndex]
                    : null;
                var powerDisplay = powerLevel != null ? new PowerItem(powerIndex, powerLevel).DisplayName : powerIndex.ToString(CultureInfo.InvariantCulture);

                instance.SelectedPowerValue = powerIndex;
                _stateStore.Save();
                if (CurrentInstance != null && CurrentInstance.Id == instance.Id)
                {
                    LoadInstanceIntoEditor(instance);
                }

                SendVtxCommand(instance, bandItem, channelItem, powerIndex, powerDisplay);
            }
            catch (Exception ex)
            {
                SetStatus("Quick send failed: " + ex.Message);
                AppendLog("Quick send failed: " + ex.Message);
            }
        }

        private void SendVtxCommand(VtxControlInstanceState instance, BandItem bandItem, ChannelItem channelItem, int powerIndex, string powerDisplay)
        {
            var request = new MAVLink.mavlink_command_long_t
            {
                target_system = instance.TargetSystem,
                target_component = instance.TargetComponent,
                command = Esp32VtxCommandId,
                confirmation = 0,
                param1 = instance.VtxNodeId,
                param2 = instance.VtxDeviceId,
                param3 = bandItem.Index + 1,
                param4 = channelItem.Index + 1,
                param5 = powerIndex,
                param6 = instance.Flags,
                param7 = 0f,
            };

            MainV2.comPort.sendPacket(request, instance.TargetSystem, instance.TargetComponent);

            var status = string.Format(
                CultureInfo.InvariantCulture,
                "Sent 31001 to {0}/{1} for node={2} device={3} band={4} channel={5} power={6}.",
                instance.TargetSystem,
                instance.TargetComponent,
                instance.VtxNodeId,
                instance.VtxDeviceId,
                bandItem.BandDisplay,
                channelItem.DisplayName,
                powerDisplay);
            SetStatus(status);
            AppendLog(status);
        }

        private string BuildMapQuickLayoutKey()
        {
            var visibleInstances = _stateStore.State.Instances != null
                ? _stateStore.State.Instances.Where(item => item.ShowOnMap).ToList()
                : new List<VtxControlInstanceState>();

            if (visibleInstances.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                "|",
                visibleInstances.Select(
                    instance =>
                    {
                        var table = FindTableById(instance.TableId);
                        var powerKey = table != null && table.Table != null && table.Table.VtxTable != null && table.Table.VtxTable.PowerlevelsList != null
                            ? string.Join(
                                ",",
                                table.Table.VtxTable.PowerlevelsList.Select(
                                    power => (power.Label ?? string.Empty) + ":" + power.Value.ToString(CultureInfo.InvariantCulture)))
                            : "notable";

                        return string.Join(
                            "~",
                            new[]
                            {
                                instance.Id ?? string.Empty,
                                instance.Name ?? string.Empty,
                                instance.TableId ?? string.Empty,
                                instance.SelectedBandIndex.ToString(CultureInfo.InvariantCulture),
                                instance.SelectedChannelIndex.ToString(CultureInfo.InvariantCulture),
                                instance.SelectedPowerValue.ToString(CultureInfo.InvariantCulture),
                                powerKey
                            });
                    }));
        }

        private void RebuildMapQuickControls(string layoutKey)
        {
            if (_mapQuickPanel == null || _mapQuickPanel.IsDisposed)
            {
                return;
            }

            var visibleInstances = _stateStore.State.Instances != null
                ? _stateStore.State.Instances.Where(item => item.ShowOnMap).ToList()
                : new List<VtxControlInstanceState>();

            _mapQuickPanel.SuspendLayout();
            try
            {
                _mapQuickPanel.Controls.Clear();

                if (visibleInstances.Count == 0)
                {
                    _mapQuickLayoutKey = layoutKey;
                    _mapQuickPanel.Visible = false;
                    return;
                }

                foreach (var instance in visibleInstances)
                {
                    var row = new FlowLayoutPanel();
                    row.AutoSize = true;
                    row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    row.WrapContents = false;
                    row.BackColor = Color.Transparent;
                    row.Margin = new Padding(0, 0, 0, 4);

                    var nameLabel = new Label();
                    nameLabel.AutoSize = true;
                    nameLabel.ForeColor = Color.White;
                    nameLabel.Font = new Font(nameLabel.Font, FontStyle.Bold);
                    nameLabel.Margin = new Padding(0, 7, 10, 0);
                    nameLabel.Text = string.IsNullOrWhiteSpace(instance.Name) ? "VTX" : instance.Name;
                    row.Controls.Add(nameLabel);

                    var table = FindTableById(instance.TableId);
                    if (table != null && table.Table != null && table.Table.VtxTable != null && table.Table.VtxTable.PowerlevelsList != null && table.Table.VtxTable.PowerlevelsList.Count > 0)
                    {
                        foreach (var powerLevel in table.Table.VtxTable.PowerlevelsList)
                        {
                            var powerButton = new MyButton();
                            powerButton.AutoSize = false;
                            powerButton.Size = new Size(54, 24);
                            powerButton.Margin = new Padding(0, 0, 4, 0);
                            var powerLevelIndex = table.Table.VtxTable.PowerlevelsList.IndexOf(powerLevel);
                            powerButton.Text = new PowerItem(powerLevelIndex, powerLevel).DisplayName;
                            if (instance.SelectedPowerValue == powerLevelIndex)
                            {
                                powerButton.Font = new Font(powerButton.Font, FontStyle.Bold);
                                powerButton.BGGradTop = Color.FromArgb(255, 197, 84);
                                powerButton.BGGradBot = Color.FromArgb(255, 224, 130);
                                powerButton.Outline = Color.FromArgb(184, 96, 0);
                                powerButton.TextColor = Color.FromArgb(70, 35, 0);
                                powerButton.ColorMouseOver = Color.FromArgb(45, 255, 255, 255);
                                powerButton.ColorMouseDown = Color.FromArgb(80, 120, 60, 0);
                            }

                            var instanceCopy = instance;
                            var powerIndexCopy = powerLevelIndex;
                            powerButton.Click += delegate { SendQuickPower(instanceCopy, powerIndexCopy); };
                            row.Controls.Add(powerButton);
                        }
                    }
                    else
                    {
                        var missingLabel = new Label();
                        missingLabel.AutoSize = true;
                        missingLabel.ForeColor = Color.Gainsboro;
                        missingLabel.Margin = new Padding(0, 7, 0, 0);
                        missingLabel.Text = "No table";
                        row.Controls.Add(missingLabel);
                    }

                    _mapQuickPanel.Controls.Add(row);
                }

                _mapQuickLayoutKey = layoutKey;
                _mapQuickPanel.Visible = true;
                PositionMapQuickPanel();
                _mapQuickPanel.BringToFront();
            }
            finally
            {
                _mapQuickPanel.ResumeLayout();
            }
        }

        private void MapQuickParentOnResize(object sender, EventArgs e)
        {
            PositionMapQuickPanel();
        }

        private void PositionMapQuickPanel()
        {
            if (_mapQuickPanel == null || _mapQuickParent == null || _mapQuickPanel.IsDisposed)
            {
                return;
            }

            _mapQuickPanel.Left = 12;
            _mapQuickPanel.Top = Math.Max(12, _mapQuickParent.ClientSize.Height - _mapQuickPanel.Height - 12);
        }

        private void AppendLog(string message)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
        }

        private static Control BuildCompactFieldRow(string label, Control control)
        {
            return BuildCompactFieldRow(label, control, null);
        }

        private static Control BuildCompactFieldRow(string label, Control control, string hint)
        {
            var row = new FlowLayoutPanel();
            row.Dock = DockStyle.Top;
            row.AutoSize = true;
            row.WrapContents = false;
            row.Margin = new Padding(0, 2, 0, 2);

            var labelControl = new Label();
            labelControl.Text = label;
            labelControl.AutoSize = true;
            labelControl.ForeColor = Color.White;
            labelControl.Width = 76;
            labelControl.Margin = new Padding(0, 7, 6, 0);
            row.Controls.Add(labelControl);

            control.Margin = new Padding(0, 3, 8, 3);
            row.Controls.Add(control);

            if (!string.IsNullOrWhiteSpace(hint))
            {
                var hintLabel = new Label();
                hintLabel.Text = hint;
                hintLabel.AutoSize = true;
                hintLabel.ForeColor = Color.White;
                hintLabel.Margin = new Padding(0, 7, 0, 0);
                row.Controls.Add(hintLabel);
            }

            return row;
        }

        private static Control BuildCompactPairRow(string label1, Control control1, string label2, Control control2)
        {
            var row = new FlowLayoutPanel();
            row.Dock = DockStyle.Top;
            row.AutoSize = true;
            row.WrapContents = false;
            row.Margin = new Padding(0, 2, 0, 2);

            row.Controls.Add(BuildInlineLabel(label1, 76));
            control1.Margin = new Padding(0, 3, 16, 3);
            row.Controls.Add(control1);

            row.Controls.Add(BuildInlineLabel(label2, 52));
            control2.Margin = new Padding(0, 3, 0, 3);
            row.Controls.Add(control2);

            return row;
        }

        private static Control BuildCompactTripleRow(string label1, Control control1, string label2, Control control2, string label3, Control control3)
        {
            var row = new FlowLayoutPanel();
            row.Dock = DockStyle.Top;
            row.AutoSize = true;
            row.WrapContents = false;
            row.Margin = new Padding(0, 2, 0, 2);

            row.Controls.Add(BuildInlineLabel(label1, 48));
            control1.Margin = new Padding(0, 3, 12, 3);
            row.Controls.Add(control1);

            row.Controls.Add(BuildInlineLabel(label2, 58));
            control2.Margin = new Padding(0, 3, 12, 3);
            row.Controls.Add(control2);

            row.Controls.Add(BuildInlineLabel(label3, 48));
            control3.Margin = new Padding(0, 3, 0, 3);
            row.Controls.Add(control3);

            return row;
        }

        private void PopulateTableSelector(ComboBox selector, string tableId)
        {
            selector.BeginUpdate();
            try
            {
                selector.Items.Clear();
                foreach (var table in _stateStore.State.Tables)
                {
                    selector.Items.Add(new TableListItem(table));
                }
            }
            finally
            {
                selector.EndUpdate();
            }

            SelectTableById(selector, tableId);
        }

        private void SelectTableById(ComboBox selector, string tableId)
        {
            if (string.IsNullOrWhiteSpace(tableId))
            {
                selector.SelectedIndex = -1;
                return;
            }

            for (var index = 0; index < selector.Items.Count; index++)
            {
                var item = selector.Items[index] as TableListItem;
                if (item != null && item.Entry.Id == tableId)
                {
                    selector.SelectedIndex = index;
                    return;
                }
            }

            selector.SelectedIndex = -1;
        }

        private VtxTableLibraryEntry FindTableById(string tableId)
        {
            if (string.IsNullOrWhiteSpace(tableId))
            {
                return null;
            }

            return _stateStore.State.Tables.FirstOrDefault(item => item.Id == tableId);
        }

        private static Label BuildInlineLabel(string text, int width)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.ForeColor = Color.White;
            label.Width = width;
            label.Height = 22;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = new Padding(0, 3, 6, 0);
            return label;
        }

        private static void AddLabeled(TableLayoutPanel layout, int row, string label, Control control, int columnSpan)
        {
            var labelControl = new Label();
            labelControl.Text = label;
            labelControl.AutoSize = true;
            labelControl.Margin = new Padding(0, 6, 6, 0);
            layout.Controls.Add(labelControl, 0, row);

            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(control, 1, row);
            layout.SetColumnSpan(control, columnSpan);
        }

        private static void AddLabeled(TableLayoutPanel layout, int row, string label, Control control)
        {
            var labelColumn = 0;
            if (row >= 0 && layout.Controls.Count > 0)
            {
                var rowControls = layout.Controls.Cast<Control>().Where(controlItem => layout.GetRow(controlItem) == row).ToList();
                labelColumn = rowControls.Count == 0 ? 0 : 2;
            }

            var labelControl = new Label();
            labelControl.Text = label;
            labelControl.AutoSize = true;
            labelControl.Margin = new Padding(0, 6, 6, 0);
            layout.Controls.Add(labelControl, labelColumn, row);

            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(control, labelColumn + 1, row);
        }

        private static void ConfigureNumeric(NumericUpDown control, int minimum, int maximum)
        {
            control.Minimum = minimum;
            control.Maximum = maximum;
            control.Width = 64;
        }

        private static void ConfigureCompactDropDown(ComboBox control, int width)
        {
            control.Width = width;
            control.Margin = new Padding(0, 3, 0, 3);
        }

        private static void ConfigureCompactText(TextBox control, int width)
        {
            control.Width = width;
            control.Margin = new Padding(0, 3, 0, 3);
        }

        private static NumericUpDown CreateDialogNumeric(int minimum, int maximum, int value)
        {
            var control = new NumericUpDown();
            ConfigureNumeric(control, minimum, maximum);
            control.Value = ClampToNumeric(control, value);
            return control;
        }

        private static decimal ClampToNumeric(NumericUpDown control, int value)
        {
            return Math.Min(control.Maximum, Math.Max(control.Minimum, value));
        }

        private static void ValidateTable(VtxTableDocument table, string filePath)
        {
            if (table == null || table.VtxTable == null)
            {
                throw new InvalidOperationException("Missing vtx_table object in " + Path.GetFileName(filePath) + ".");
            }

            if (table.VtxTable.BandsList == null || table.VtxTable.BandsList.Count == 0)
            {
                throw new InvalidOperationException("No bands found in " + Path.GetFileName(filePath) + ".");
            }

            if (table.VtxTable.PowerlevelsList == null || table.VtxTable.PowerlevelsList.Count == 0)
            {
                throw new InvalidOperationException("No power levels found in " + Path.GetFileName(filePath) + ".");
            }
        }

        private static string BuildTableName(VtxTableDocument table, string filePath)
        {
            if (!string.IsNullOrWhiteSpace(table.Description))
            {
                return table.Description.Trim();
            }

            return Path.GetFileNameWithoutExtension(filePath);
        }

        private static VtxControlInstanceState BuildDefaultInstance()
        {
            return new VtxControlInstanceState
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "VTX Instance",
                TargetSystem = DefaultTargetSystemId,
                TargetComponent = DefaultTargetComponentId,
                VtxNodeId = DefaultNodeId,
                VtxDeviceId = DefaultDeviceId,
                Flags = 0,
                SelectedBandIndex = 0,
                SelectedChannelIndex = 0,
                SelectedPowerValue = 0,
            };
        }

        private sealed class InstanceListItem
        {
            public InstanceListItem(VtxControlInstanceState state)
            {
                State = state;
            }

            public VtxControlInstanceState State { get; private set; }

            public string DisplayName
            {
                get
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}",
                        string.IsNullOrWhiteSpace(State.Name) ? "VTX Instance" : State.Name,
                        State.TargetSystem,
                        State.TargetComponent,
                        State.VtxNodeId,
                        State.VtxDeviceId);
                }
            }
        }

        private sealed class TableListItem
        {
            public TableListItem(VtxTableLibraryEntry entry)
            {
                Entry = entry;
            }

            public VtxTableLibraryEntry Entry { get; private set; }

            public override string ToString()
            {
                return Entry.Name;
            }
        }

        private sealed class BandItem
        {
            public BandItem(int index, VtxTableBand band)
            {
                Index = index;
                Band = band;
            }

            public int Index { get; private set; }

            public VtxTableBand Band { get; private set; }

            public string BandDisplay
            {
                get
                {
                    if (!string.IsNullOrWhiteSpace(Band.Letter))
                    {
                        return Band.Letter.Trim();
                    }

                    return Band.Name ?? (Index + 1).ToString(CultureInfo.InvariantCulture);
                }
            }

            public override string ToString()
            {
                if (!string.IsNullOrWhiteSpace(Band.Letter) && !string.IsNullOrWhiteSpace(Band.Name))
                {
                    return Band.Letter.Trim() + " - " + Band.Name.Trim();
                }

                return BandDisplay;
            }
        }

        private sealed class ChannelItem
        {
            public ChannelItem(int index, int frequency)
            {
                Index = index;
                Frequency = frequency;
            }

            public int Index { get; private set; }

            public int Frequency { get; private set; }

            public string DisplayName
            {
                get
                {
                    return string.Format(CultureInfo.InvariantCulture, "CH{0} ({1} MHz)", Index + 1, Frequency);
                }
            }

            public override string ToString()
            {
                return DisplayName;
            }
        }

        private sealed class PowerItem
        {
            public PowerItem(VtxTablePowerLevel powerLevel)
            {
                Index = -1;
                Value = powerLevel.Value;
                Label = powerLevel.Label;
            }

            public PowerItem(int index, VtxTablePowerLevel powerLevel)
            {
                Index = index;
                Value = powerLevel.Value;
                Label = powerLevel.Label;
            }

            public int Index { get; private set; }

            public int Value { get; private set; }

            public string Label { get; private set; }

            public string DisplayName
            {
                get
                {
                    if (string.IsNullOrWhiteSpace(Label))
                    {
                        return Value.ToString(CultureInfo.InvariantCulture);
                    }

                    return Label.Trim();
                }
            }

            public override string ToString()
            {
                return DisplayName;
            }
        }
    }

    internal sealed class VtxPluginStateStore
    {
        private readonly string _filePath;

        public VtxPluginStateStore()
        {
            var dataDir = Settings.GetDataDirectory();
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                dataDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
            }

            _filePath = Path.Combine(dataDir, "esp32-vtx-control-state.json");
            State = Load();
        }

        public VtxPluginState State { get; private set; }

        public void Save()
        {
            var json = JsonConvert.SerializeObject(State, Formatting.Indented);
            File.WriteAllText(_filePath, json);
        }

        private VtxPluginState Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new VtxPluginState();
                }

                var json = File.ReadAllText(_filePath);
                var state = JsonConvert.DeserializeObject<VtxPluginState>(json);
                return state ?? new VtxPluginState();
            }
            catch
            {
                return new VtxPluginState();
            }
        }
    }

    internal sealed class VtxPluginState
    {
        public List<VtxTableLibraryEntry> Tables { get; set; }

        public List<VtxControlInstanceState> Instances { get; set; }

        public string SelectedInstanceId { get; set; }

        public VtxPluginLayoutState Layout { get; set; }
    }

    internal sealed class VtxPluginLayoutState
    {
        public bool Detached { get; set; }

        public int WindowLeft { get; set; }

        public int WindowTop { get; set; }

        public int WindowWidth { get; set; }

        public int WindowHeight { get; set; }
    }

    internal sealed class VtxTableLibraryEntry
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string SourcePath { get; set; }

        public DateTime ImportedUtc { get; set; }

        public VtxTableDocument Table { get; set; }
    }

    internal sealed class VtxControlInstanceState
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public byte TargetSystem { get; set; }

        public byte TargetComponent { get; set; }

        public byte VtxNodeId { get; set; }

        public byte VtxDeviceId { get; set; }

        public byte Flags { get; set; }

        public bool ShowOnMap { get; set; }

        public string TableId { get; set; }

        public int SelectedBandIndex { get; set; }

        public int SelectedChannelIndex { get; set; }

        // Stored as zero-based power index. Older state files may still contain raw power values.
        public int SelectedPowerValue { get; set; }
    }

    internal sealed class VtxTableDocument
    {
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("vtx_table")]
        public VtxTableDefinition VtxTable { get; set; }
    }

    internal sealed class VtxTableDefinition
    {
        [JsonProperty("bands_list")]
        public List<VtxTableBand> BandsList { get; set; }

        [JsonProperty("powerlevels_list")]
        public List<VtxTablePowerLevel> PowerlevelsList { get; set; }
    }

    internal sealed class VtxTableBand
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("letter")]
        public string Letter { get; set; }

        [JsonProperty("frequencies")]
        public List<int> Frequencies { get; set; }
    }

    internal sealed class VtxTablePowerLevel
    {
        [JsonProperty("value")]
        public int Value { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }
    }
}