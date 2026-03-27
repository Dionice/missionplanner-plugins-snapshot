using System;
using System.Windows.Forms;
using System.Drawing;
using MissionPlanner.Plugin;
using MissionPlanner;
using MissionPlanner.Controls;
using MissionPlanner.GCSViews;
using MissionPlanner.Utilities;

namespace MissionPlanner.plugins
{
    public class VTXControlUI : UserControl, IActivate
    {
        private Label lblStatus;
        private FlowLayoutPanel pnlChannels;
        private FlowLayoutPanel pnlPowers;
        private Font headerFont;
        private int currentPowerIndex = 1;
        private int currentChannelInBand = 0;
        private int lastSelectedBand = 0;

        // Frequency table: band A (8 channels) followed by band B (only unique channels)
        private static readonly int[] freqTable = new int[] {
            1080, 1120, 1160, 1200, 1240, 1280, 1320, 1360,
            1220, 1258, 1300, 1340
        };
        private static readonly string[] powerLabels = new string[] { "25 mW", "200 mW", "1 W", "4 W" };
        private static readonly int[] channelsPerBand = new int[] { 8, 4 };
        private static readonly int[][] displayChannelNumbers = new int[][] {
            new int[] {1,2,3,4,5,6,7,8},
            new int[] {2,4,6,8}
        };

        private int bandOffset(int band)
        {
            return band == 0 ? 0 : channelsPerBand[0];
        }

        public VTXControlUI()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // status label at bottom (appear below panels)
            this.lblStatus = new Label() { Dock = DockStyle.Bottom, Height = 24, Text = "", AutoSize = false, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };

            // channel panel: auto-size height based on buttons, wrap to next line when full
            this.pnlChannels = new FlowLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(2),
                Margin = new Padding(2, 0, 2, 2),
                // constrain width so only 4 channel buttons fit per row (70px button + spacing)
                // allow multiple rows by increasing maximum height so Band B is visible
                MaximumSize = new System.Drawing.Size(308, 240)
            };
            // create reusable header font to avoid allocating many fonts repeatedly
            this.headerFont = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold);
            // adjust header widths when panel size changes so headers always span the current width
            this.pnlChannels.SizeChanged += (s, e) => { try { UpdateHeaderWidths(); } catch { } };

            // power panel: placed below channels, auto-size to content
            this.pnlPowers = new FlowLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(4),
                Margin = new Padding(2)
            };

            // add controls in logical order so panels occupy top area and status sits below them
            this.Controls.Add(pnlChannels);
            this.Controls.Add(pnlPowers);
            this.Controls.Add(lblStatus);

            // load saved selections
            try { var sc = Settings.Instance["VTX_Channel"]; if (!string.IsNullOrEmpty(sc)) { int gi = Math.Max(0, Math.Min(int.Parse(sc), freqTable.Length-1)); lastSelectedBand = (gi < channelsPerBand[0]) ? 0 : 1; currentChannelInBand = gi - bandOffset(lastSelectedBand); } } catch { }
            try { var sp = Settings.Instance["VTX_Power"]; if (!string.IsNullOrEmpty(sp)) currentPowerIndex = Math.Min(Math.Max(int.Parse(sp), 0), 3); } catch { }

            UpdateChannelButtons();
            CreatePowerButtons();
            this.Load += (s, e) => { try { this.BeginInvoke((Action)UpdateSelectionVisuals); } catch { UpdateSelectionVisuals(); } }; 
        }

        public void Activate()
        {
            try { var sc = Settings.Instance["VTX_Channel"]; if (!string.IsNullOrEmpty(sc)) { int gi = Math.Max(0, Math.Min(int.Parse(sc), freqTable.Length-1)); lastSelectedBand = (gi < channelsPerBand[0]) ? 0 : 1; currentChannelInBand = gi - bandOffset(lastSelectedBand); } } catch { }
            try { var sp = Settings.Instance["VTX_Power"]; if (!string.IsNullOrEmpty(sp)) currentPowerIndex = Math.Min(Math.Max(int.Parse(sp), 0), 3); } catch { }
            UpdateChannelButtons();
            UpdateSelectionVisuals();
        }



        private void SendIndex(int band, int channelInBand, int powerIndex)
        {
            int global = bandOffset(band) + channelInBand;
            // Use firmware-compatible mapping: TOTAL_COMBOS = channels * powers (12 * 4 = 48)
            int channelsTotal = freqTable.Length; // 12 (8 A + 4 B)
            int totalCombos = channelsTotal * powerLabels.Length; // 48
            int idx = powerIndex * channelsTotal + global; // 0..47
            const int servoMin = 500;
            const int servoMax = 2500;
            double span = servoMax - servoMin;
            double step = span / (double)totalCombos;
            int pwm = servoMin + (int)Math.Round((idx + 0.5) * step);
            try
            {
                bool ok = MainV2.comPort.doCommand((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent,
                    MAVLink.MAV_CMD.DO_SET_SERVO, 6, pwm, 0, 0, 0, 0, 0);
                int gi = global;
                int freq = (gi >= 0 && gi < freqTable.Length) ? freqTable[gi] : 0;
                int displayNum = (band >= 0 && band < displayChannelNumbers.Length && channelInBand < displayChannelNumbers[band].Length) ? displayChannelNumbers[band][channelInBand] : (channelInBand + 1);
                string pLabel = (powerIndex >= 0 && powerIndex < powerLabels.Length) ? powerLabels[powerIndex] : (powerIndex.ToString() + " pwr");
                if (ok) lblStatus.Text = $"Sent servo=6 pwm={pwm} (range={servoMin}-{servoMax}) — CH{displayNum} {freq} MHz — {pLabel}";
                else lblStatus.Text = $"Failed to send PWM (power={pLabel}).";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error sending PWM: " + ex.Message;
            }
        }

        private void UpdateChannelButtons()
        {
            pnlChannels.Controls.Clear();
            for (int bandIndex = 0; bandIndex < channelsPerBand.Length; bandIndex++)
            {
                var hdr = new Label();
                hdr.Text = (bandIndex == 0 ? "Band A" : "Band B");
                hdr.AutoSize = false;
                hdr.Height = 18;
                // make header span the panel width so it starts at the beginning of the line
                hdr.Tag = "bandHeader";
                int hdrWidth = (pnlChannels.ClientSize.Width > 0) ? pnlChannels.ClientSize.Width - 8 : ((pnlChannels.MaximumSize.Width > 0) ? pnlChannels.MaximumSize.Width - 8 : 300);
                hdr.Width = hdrWidth;
                hdr.Margin = new Padding(2, 4, 2, 4);
                hdr.Font = this.headerFont;
                hdr.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
                pnlChannels.Controls.Add(hdr);
                // force header to occupy its own row above channel buttons
                pnlChannels.SetFlowBreak(hdr, true);
                int count = channelsPerBand[bandIndex];
                int offset = bandOffset(bandIndex);
                for (int i = 0; i < count; i++)
                {
                    int freq = 0;
                    int gi = offset + i;
                    if (gi >= 0 && gi < freqTable.Length) freq = freqTable[gi];
                    int displayNum = (bandIndex >= 0 && bandIndex < displayChannelNumbers.Length && i < displayChannelNumbers[bandIndex].Length) ? displayChannelNumbers[bandIndex][i] : (i + 1);
                    var b = new Button() { Text = string.Format("{0}: {1} MHz", displayNum, freq), Width = 70, Height = 24, Tag = new Tuple<int,int>(bandIndex, i), UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat };
                    int localI = i; int localBand = bandIndex;
                    b.Click += (s, e) => {
                        currentChannelInBand = localI; lastSelectedBand = localBand;
                        int gi2 = bandOffset(lastSelectedBand) + currentChannelInBand;
                        Settings.Instance["VTX_Channel"] = gi2.ToString(); Settings.Instance.Save();
                        SendIndex(lastSelectedBand, currentChannelInBand, currentPowerIndex);
                        UpdateSelectionVisuals();
                    };
                    pnlChannels.Controls.Add(b);
                }
            }
            UpdateSelectionVisuals();
        }

        private void UpdateHeaderWidths()
        {
            int hdrWidth = (pnlChannels.ClientSize.Width > 0) ? pnlChannels.ClientSize.Width - 8 : ((pnlChannels.MaximumSize.Width > 0) ? pnlChannels.MaximumSize.Width - 8 : 300);
            foreach (Control c in pnlChannels.Controls)
            {
                if (c is Label lab && lab.Tag is string t && t == "bandHeader")
                {
                    lab.Width = hdrWidth;
                }
            }
        }

        private void CreatePowerButtons()
        {
            pnlPowers.Controls.Clear();
            string[] labels = new string[] { "25 mW", "200 mW", "1 W", "4 W" };
            for (int i = 0; i < labels.Length; i++)
            {
                var b = new Button() { Text = labels[i], Width = 50, Height = 24, Tag = i, UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat };
                int localI = i;
                b.Click += (s, e) => { currentPowerIndex = localI; Settings.Instance["VTX_Power"] = currentPowerIndex.ToString(); Settings.Instance.Save(); SendIndex(lastSelectedBand, currentChannelInBand, currentPowerIndex); UpdateSelectionVisuals(); };
                pnlPowers.Controls.Add(b);
            }
            UpdateSelectionVisuals();
        }

        private void UpdateSelectionVisuals()
        {
            int savedGlobal = bandOffset(lastSelectedBand) + currentChannelInBand;
            foreach (Control c in pnlChannels.Controls)
            {
                if (c is Button b)
                {
                    var tag = b.Tag as Tuple<int,int>;
                    if (tag != null)
                    {
                        int gi = bandOffset(tag.Item1) + tag.Item2;
                        if (gi == savedGlobal) b.BackColor = Color.LightSkyBlue;
                        else b.BackColor = SystemColors.Control;
                    }
                }
            }
            foreach (Control c in pnlPowers.Controls)
            {
                if (c is Button b)
                {
                    int idx = (int)b.Tag;
                    if (idx == currentPowerIndex) b.BackColor = Color.LightSkyBlue;
                    else b.BackColor = SystemColors.Control;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (this.headerFont != null) { this.headerFont.Dispose(); this.headerFont = null; } } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // UI for VTX2 (64-channel bands, 5 power levels)
    public class VTXControlUI2 : UserControl, IActivate
    {
        private Label lblStatus;
        private FlowLayoutPanel pnlChannels;
        private FlowLayoutPanel pnlPowers;
        private Font headerFont;
        private int currentPowerIndex = 0;
        private int currentChannelInBand = 0;
        private int lastSelectedBand = 0;

        // VTX2 frequency table (bands A,B,E,F,R,P,H,U) - 8 bands × 8 channels
        private static readonly int[] freqTable = new int[] {
            3000,3030,3060,3090,3120,3150,3180,3210,
            3240,3270,3300,3330,3370,3400,3430,3470,
            3500,3530,3560,3590,3620,3650,3680,3710,
            3740,3770,3800,3830,3860,3890,3920,3950,
            3980,4010,4040,4070,4100,4130,4160,4190,
            4220,4250,4280,4310,4340,4370,4400,4430,
            4470,4500,4530,4560,4590,4620,4650,4680,
            4710,4740,4770,4812,4839,4872,4911,4938
        };
        private static readonly string[] powerLabels = new string[] { "25 mW", "200 mW", "500 mW", "1 W", "3 W" };
        private static readonly int[] channelsPerBand = new int[] { 8,8,8,8,8,8,8,8 };
        private static readonly int[][] displayChannelNumbers = new int[][] {
            new int[] {1,2,3,4,5,6,7,8}, new int[] {1,2,3,4,5,6,7,8}, new int[] {1,2,3,4,5,6,7,8}, new int[] {1,2,3,4,5,6,7,8},
            new int[] {1,2,3,4,5,6,7,8}, new int[] {1,2,3,4,5,6,7,8}, new int[] {1,2,3,4,5,6,7,8}, new int[] {1,2,3,4,5,6,7,8}
        };

        private int bandOffset(int band)
        {
            int off = 0;
            for (int i = 0; i < band; i++) off += channelsPerBand[i];
            return off;
        }

        public VTXControlUI2()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.lblStatus = new Label() { Dock = DockStyle.Bottom, Height = 24, Text = "", AutoSize = false, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            this.pnlChannels = new FlowLayoutPanel() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(2), Margin = new Padding(2,0,2,2), MaximumSize = new System.Drawing.Size(520, 400) };
            this.headerFont = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold);
            this.pnlChannels.SizeChanged += (s, e) => { try { UpdateHeaderWidths(); } catch { } };
            this.pnlPowers = new FlowLayoutPanel() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4), Margin = new Padding(2) };

            this.Controls.Add(pnlChannels);
            this.Controls.Add(pnlPowers);
            this.Controls.Add(lblStatus);

            try { var sc = Settings.Instance["VTX2_Channel"]; if (!string.IsNullOrEmpty(sc)) { int gi = Math.Max(0, Math.Min(int.Parse(sc), freqTable.Length-1)); lastSelectedBand = 0; int acc = 0; for (int b=0;b<channelsPerBand.Length;b++){ if (gi < acc + channelsPerBand[b]) { lastSelectedBand = b; currentChannelInBand = gi - acc; break; } acc += channelsPerBand[b]; } } } catch { }
            try { var sp = Settings.Instance["VTX2_Power"]; if (!string.IsNullOrEmpty(sp)) currentPowerIndex = Math.Min(Math.Max(int.Parse(sp), 0), powerLabels.Length-1); } catch { }

            UpdateChannelButtons();
            CreatePowerButtons();
            this.Load += (s, e) => { try { this.BeginInvoke((Action)UpdateSelectionVisuals); } catch { UpdateSelectionVisuals(); } };
        }

        public void Activate()
        {
            try { var sc = Settings.Instance["VTX2_Channel"]; if (!string.IsNullOrEmpty(sc)) { int gi = Math.Max(0, Math.Min(int.Parse(sc), freqTable.Length-1)); lastSelectedBand = 0; int acc = 0; for (int b=0;b<channelsPerBand.Length;b++){ if (gi < acc + channelsPerBand[b]) { lastSelectedBand = b; currentChannelInBand = gi - acc; break; } acc += channelsPerBand[b]; } } } catch { }
            try { var sp = Settings.Instance["VTX2_Power"]; if (!string.IsNullOrEmpty(sp)) currentPowerIndex = Math.Min(Math.Max(int.Parse(sp), 0), powerLabels.Length-1); } catch { }
            UpdateChannelButtons();
            UpdateSelectionVisuals();
        }

        private void SendIndex(int band, int channelInBand, int powerIndex)
        {
            int global = bandOffset(band) + channelInBand;
            int channelsTotal = freqTable.Length; // 64
            int totalCombos = channelsTotal * powerLabels.Length; // 64 * 5 = 320
            int idx = powerIndex * channelsTotal + global;
            const int servoMin = 500; const int servoMax = 2500;
            double span = servoMax - servoMin; double step = span / (double)totalCombos;
            int pwm = servoMin + (int)Math.Round((idx + 0.5) * step);
            try
            {
                const int servoOutput = 11; // use servo output 11 for VTX2
                bool ok = MainV2.comPort.doCommand((byte)MainV2.comPort.sysidcurrent, (byte)MainV2.comPort.compidcurrent,
                    MAVLink.MAV_CMD.DO_SET_SERVO, servoOutput, pwm, 0, 0, 0, 0, 0);
                int gi = global; int freq = (gi >= 0 && gi < freqTable.Length) ? freqTable[gi] : 0;
                int displayNum = (band >=0 && band < displayChannelNumbers.Length && channelInBand < displayChannelNumbers[band].Length) ? displayChannelNumbers[band][channelInBand] : (channelInBand+1);
                string pLabel = (powerIndex >=0 && powerIndex < powerLabels.Length) ? powerLabels[powerIndex] : (powerIndex.ToString()+" pwr");
                if (ok) lblStatus.Text = $"Sent servo={servoOutput} pwm={pwm} (range={servoMin}-{servoMax}) — CH{displayNum} {freq} MHz — {pLabel}";
                else lblStatus.Text = $"Failed to send PWM (power={pLabel}).";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error sending PWM: " + ex.Message;
            }
        }

        private void UpdateChannelButtons()
        {
            pnlChannels.Controls.Clear();
            for (int bandIndex = 0; bandIndex < channelsPerBand.Length; bandIndex++)
            {
                var hdr = new Label(); hdr.Text = "Band " + (char)('A' + bandIndex); hdr.AutoSize = false; hdr.Height = 18; hdr.Tag = "bandHeader";
                int hdrWidth = (pnlChannels.ClientSize.Width > 0) ? pnlChannels.ClientSize.Width - 8 : ((pnlChannels.MaximumSize.Width > 0) ? pnlChannels.MaximumSize.Width - 8 : 500);
                hdr.Width = hdrWidth; hdr.Margin = new Padding(2,4,2,4); hdr.Font = this.headerFont; hdr.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
                pnlChannels.Controls.Add(hdr); pnlChannels.SetFlowBreak(hdr, true);
                int count = channelsPerBand[bandIndex]; int offset = bandOffset(bandIndex);
                for (int i = 0; i < count; i++)
                {
                    int freq = 0; int gi = offset + i; if (gi >= 0 && gi < freqTable.Length) freq = freqTable[gi]; int displayNum = (bandIndex >=0 && bandIndex < displayChannelNumbers.Length && i < displayChannelNumbers[bandIndex].Length) ? displayChannelNumbers[bandIndex][i] : (i+1);
                    var b = new Button() { Text = string.Format("{0}: {1} MHz", displayNum, freq), Width = 80, Height = 24, Tag = new Tuple<int,int>(bandIndex, i), UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat };
                    int localI = i; int localBand = bandIndex;
                    b.Click += (s, e) => { currentChannelInBand = localI; lastSelectedBand = localBand; int gi2 = bandOffset(lastSelectedBand) + currentChannelInBand; Settings.Instance["VTX2_Channel"] = gi2.ToString(); Settings.Instance.Save(); SendIndex(lastSelectedBand, currentChannelInBand, currentPowerIndex); UpdateSelectionVisuals(); };
                    pnlChannels.Controls.Add(b);
                }
            }
            UpdateSelectionVisuals();
        }

        private void UpdateHeaderWidths()
        {
            int hdrWidth = (pnlChannels.ClientSize.Width > 0) ? pnlChannels.ClientSize.Width - 8 : ((pnlChannels.MaximumSize.Width > 0) ? pnlChannels.MaximumSize.Width - 8 : 500);
            foreach (Control c in pnlChannels.Controls) if (c is Label lab && lab.Tag is string t && t == "bandHeader") lab.Width = hdrWidth;
        }

        private void CreatePowerButtons()
        {
            pnlPowers.Controls.Clear();
            string[] labels = powerLabels;
            for (int i = 0; i < labels.Length; i++) { var b = new Button() { Text = labels[i], Width = 60, Height = 24, Tag = i, UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat }; int localI = i; b.Click += (s,e) => { currentPowerIndex = localI; Settings.Instance["VTX2_Power"] = currentPowerIndex.ToString(); Settings.Instance.Save(); SendIndex(lastSelectedBand, currentChannelInBand, currentPowerIndex); UpdateSelectionVisuals(); }; pnlPowers.Controls.Add(b); }
            UpdateSelectionVisuals();
        }

        private void UpdateSelectionVisuals()
        {
            int savedGlobal = bandOffset(lastSelectedBand) + currentChannelInBand;
            foreach (Control c in pnlChannels.Controls) if (c is Button b) { var tag = b.Tag as Tuple<int,int>; if (tag != null) { int gi = bandOffset(tag.Item1) + tag.Item2; b.BackColor = (gi == savedGlobal) ? Color.LightSkyBlue : SystemColors.Control; } }
            foreach (Control c in pnlPowers.Controls) if (c is Button b) { int idx = (int)b.Tag; b.BackColor = (idx == currentPowerIndex) ? Color.LightSkyBlue : SystemColors.Control; }
        }

        protected override void Dispose(bool disposing) { if (disposing) { try { if (this.headerFont != null) { this.headerFont.Dispose(); this.headerFont = null; } } catch { } } base.Dispose(disposing); }
    }

    // Plugin wrapper so Mission Planner discovers the plugin in the Plugins list
    public class VTXControlPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "VTXControl"; } }
        public override string Version { get { return "0.1"; } }
        public override string Author { get { return "Dionice"; } }

        private ToolStripMenuItem rootMenu;
        private TabPage tabPage;
        private TabPage tabPage2;

        public override bool Init()
        {
            this.loopratehz = 1;
            return true;
        }

        public override bool Loaded()
        {
            try
            {
                rootMenu = new ToolStripMenuItem("VTXControl");
                var open = new ToolStripMenuItem("Open VTX Control");
                open.Click += (s, e) => {
                    var f = new Form();
                    f.Text = "VTX Control";
                    var ui = new VTXControlUI();
                    ui.Dock = DockStyle.Fill;
                    f.ClientSize = new System.Drawing.Size(520, 240);
                    f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                    f.Controls.Add(ui);
                    // ensure the popup form is disposed when closed
                    f.FormClosed += (fs, fe) => { try { fs?.GetType(); ((Form)fs).Dispose(); } catch { } };
                    f.Show(MainV2.instance);
                };
                rootMenu.DropDownItems.Add(open);

                var open2 = new ToolStripMenuItem("Open VTX2 Control");
                open2.Click += (s, e) => {
                    var f = new Form();
                    f.Text = "VTX2 Control";
                    var ui = new VTXControlUI2();
                    ui.Dock = DockStyle.Fill;
                    f.ClientSize = new System.Drawing.Size(760, 400);
                    f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                    f.Controls.Add(ui);
                    f.FormClosed += (fs, fe) => { try { fs?.GetType(); ((Form)fs).Dispose(); } catch { } };
                    f.Show(MainV2.instance);
                };
                rootMenu.DropDownItems.Add(open2);
                MainV2.instance.MainMenu.Items.Add(rootMenu);

                // create a FlightData tab so the plugin appears in the FlightData tabs list
                Action createTab = () =>
                {
                    try
                    {
                        if (Host?.MainForm == null || Host.MainForm.FlightData == null) return;
                        tabPage = new TabPage();
                        tabPage.Text = "VTX Control";
                        tabPage.Name = "tabVTXControl";
                        var ui = new VTXControlUI();
                        ui.Dock = DockStyle.Fill;
                        tabPage.Controls.Add(ui);

                        Host.MainForm.FlightData.TabListOriginal.Add(tabPage);
                        var tabctrl = Host.MainForm.FlightData.tabControlactions;
                        if (!tabctrl.TabPages.Contains(tabPage)) tabctrl.TabPages.Insert(Math.Min(5, tabctrl.TabPages.Count), tabPage);
                        ThemeManager.ApplyThemeTo(tabPage);
                    }
                    catch { }
                };

                Action createTab2 = () =>
                {
                    try
                    {
                        if (Host?.MainForm == null || Host.MainForm.FlightData == null) return;
                        tabPage2 = new TabPage();
                        tabPage2.Text = "VTX2 Control";
                        tabPage2.Name = "tabVTX2Control";
                        var ui2 = new VTXControlUI2();
                        ui2.Dock = DockStyle.Fill;
                        tabPage2.Controls.Add(ui2);

                        Host.MainForm.FlightData.TabListOriginal.Add(tabPage2);
                        var tabctrl2 = Host.MainForm.FlightData.tabControlactions;
                        if (!tabctrl2.TabPages.Contains(tabPage2)) tabctrl2.TabPages.Insert(Math.Min(6, tabctrl2.TabPages.Count), tabPage2);
                        ThemeManager.ApplyThemeTo(tabPage2);
                    }
                    catch { }
                };

                if (FlightData.instance != null)
                {
                    try { FlightData.instance.BeginInvoke(createTab); } catch { createTab(); }
                    try { FlightData.instance.BeginInvoke(createTab2); } catch { createTab2(); }
                }
            }
            catch { }
            return true;
        }

        public override bool Exit()
        {
            try { if (rootMenu != null) MainV2.instance.MainMenu.Items.Remove(rootMenu); } catch { }
            try {
                if (tabPage != null)
                {
                    try { if (Host?.MainForm != null && Host.MainForm.FlightData != null) Host.MainForm.FlightData.TabListOriginal.Remove(tabPage); } catch { }
                    try { if (Host?.MainForm != null && Host.MainForm.FlightData != null) { var tabctrl = Host.MainForm.FlightData.tabControlactions; if (tabctrl.TabPages.Contains(tabPage)) tabctrl.TabPages.Remove(tabPage); } } catch { }
                    try { tabPage.Dispose(); } catch { }
                    tabPage = null;
                }
                if (tabPage2 != null)
                {
                    try { if (Host?.MainForm != null && Host.MainForm.FlightData != null) Host.MainForm.FlightData.TabListOriginal.Remove(tabPage2); } catch { }
                    try { if (Host?.MainForm != null && Host.MainForm.FlightData != null) { var tabctrl = Host.MainForm.FlightData.tabControlactions; if (tabctrl.TabPages.Contains(tabPage2)) tabctrl.TabPages.Remove(tabPage2); } } catch { }
                    try { tabPage2.Dispose(); } catch { }
                    tabPage2 = null;
                }
            } catch { }
            return true;
        }
    }
}
