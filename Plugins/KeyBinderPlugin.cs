using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Globalization;
using MissionPlanner;
using MissionPlanner.Utilities;
using MissionPlanner.GCSViews;

namespace KeyBinder
{
    public class KeyBinderPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "KeyBinder"; } }
        public override string Version { get { return "0.1"; } }
        public override string Author { get { return "Dionice"; } }

        private Dictionary<string, string> bindings = new Dictionary<string, string>(); // keySig -> menuPath
        private string pendingBindingSig = null;
        private Keys pendingBindingPrimary = Keys.None;
        private bool pendingRequiresCtrl = false;
        private bool pendingRequiresAlt = false;
        private bool pendingRequiresShift = false;
        private readonly object bindingsLock = new object();
        private ToolStripMenuItem _menuRoot = null;
        private bool _keyHandlersAttached = false;
        private bool _mapHandlerAttached = false;

        private const string KB_INC_TARGET_ALT = "KeyBinder: Increase Target Alt";
        private const string KB_DEC_TARGET_ALT = "KeyBinder: Decrease Target Alt";

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public override bool Init()
        {
            return true;
        }

        public override bool Loaded()
        {
            try
            {
                LoadBindings();
                var root = new ToolStripMenuItem("KeyBinder") { Name = "KeyBinderRoot" };
                var manage = new ToolStripMenuItem("Manage Bindings");
                manage.Click += (s, e) => { ShowManageDialog(); };
                root.DropDownItems.Add(manage);
                try
                {
                    if (Host?.FDMenuMap != null)
                    {
                        var found = Host.FDMenuMap.Items.Find("KeyBinderRoot", false);
                        if (found == null || found.Length == 0)
                        {
                            Host.FDMenuMap.Items.Insert(3, root);
                            _menuRoot = root;
                        }
                        else
                        {
                            _menuRoot = found[0] as ToolStripMenuItem;
                        }
                    }
                }
                catch { }

                // Attach global key handlers once
                try
                {
                    if (MainV2.instance != null && !_keyHandlersAttached)
                    {
                        MainV2.instance.KeyDown += Instance_KeyDown;
                        MainV2.instance.KeyUp += Instance_KeyUp;
                        _keyHandlersAttached = true;
                    }
                }
                catch { }

                // listen for map clicks to support hold-to-click activation (attach once)
                try
                {
                    if (FlightData.instance != null && FlightData.instance.gMapControl1 != null && !_mapHandlerAttached)
                    {
                        FlightData.instance.gMapControl1.MouseDown += GMap_MouseDown;
                        _mapHandlerAttached = true;
                    }
                }
                catch { }
            }
            catch { }
            return true;
        }

        private void GMap_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(pendingBindingSig)) return;
                if (e.Button != MouseButtons.Left) return;
                // perform the bound menu action now that the user clicked the map while holding the binding
                // verify the binding keys are still held to avoid races where KeyUp cleared pending state
                try {
                    // check modifiers
                    if (pendingRequiresCtrl && (Control.ModifierKeys & Keys.Control) == 0) return;
                    if (pendingRequiresAlt && (Control.ModifierKeys & Keys.Alt) == 0) return;
                    if (pendingRequiresShift && (Control.ModifierKeys & Keys.Shift) == 0) return;
                    // check primary key state using GetAsyncKeyState
                    if (pendingBindingPrimary != Keys.None)
                    {
                        if ((GetAsyncKeyState((int)pendingBindingPrimary) & 0x8000) == 0) return;
                    }
                } catch { }

                string path = null;
                try { lock (bindingsLock) { bindings.TryGetValue(pendingBindingSig, out path); } } catch { }
                if (!string.IsNullOrEmpty(path))
                {
                    try { FlightData.instance.gMapControl1.BeginInvoke(new Action(() => { try { InvokeBindingAction(path); } catch { } })); } catch { try { InvokeBindingAction(path); } catch { } }
                }
            }
            catch { }
            finally { pendingBindingSig = null; pendingBindingPrimary = Keys.None; pendingRequiresCtrl = pendingRequiresAlt = pendingRequiresShift = false; }
        }

        private void Instance_KeyUp(object sender, KeyEventArgs e)
        {
            try {
                // only clear pending binding when the primary key is released
                try {
                    if (pendingBindingPrimary != Keys.None)
                    {
                        if (e.KeyCode == pendingBindingPrimary)
                        {
                            pendingBindingSig = null;
                            pendingBindingPrimary = Keys.None;
                            pendingRequiresCtrl = pendingRequiresAlt = pendingRequiresShift = false;
                        }
                    }
                    else
                    {
                        // fallback: if no primary recorded, clear on any key up
                        pendingBindingSig = null;
                    }
                } catch { pendingBindingSig = null; }
            } catch { }
        }

        private void Instance_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                string sig = KeySignature(e);
                string path = null;
                try { lock (bindingsLock) { bindings.TryGetValue(sig, out path); } } catch { }
                if (!string.IsNullOrEmpty(path))
                {
                    // if this binding requires a map click while holding the keys, set pending state
                    if (IsHoldRequired(sig))
                    {
                        pendingBindingSig = sig;
                        pendingBindingPrimary = e.KeyCode;
                        pendingRequiresCtrl = e.Control;
                        pendingRequiresAlt = e.Alt;
                        pendingRequiresShift = e.Shift;
                        e.Handled = true;
                        return;
                    }
                    // otherwise invoke immediately (menu item or special KeyBinder action)
                    try { FlightData.instance.gMapControl1.BeginInvoke(new Action(() => { try { InvokeBindingAction(path); } catch { } })); } catch { try { InvokeBindingAction(path); } catch { } }
                    e.Handled = true;
                }
            }
            catch { }
        }

        private string KeySignature(KeyEventArgs e)
        {
            var parts = new List<string>();
            if (e.Control) parts.Add("Ctrl");
            if (e.Alt) parts.Add("Alt");
            if (e.Shift) parts.Add("Shift");
            parts.Add(e.KeyCode.ToString());
            return string.Join("+", parts);
        }

        private void InvokeBindingAction(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (path == KB_INC_TARGET_ALT) { AdjustTargetAlt(1.0); return; }
                if (path == KB_DEC_TARGET_ALT) { AdjustTargetAlt(-1.0); return; }
                var item = FindMenuItemByPath(path);
                if (item != null)
                {
                    try { item.PerformClick(); } catch { }
                }
            }
            catch { }
        }

        private void AdjustTargetAlt(double delta)
        {
            try
            {
                var st = Settings.Instance;
                double cur = 0.0;
                try { double.TryParse(st["Companion_target_alt"]?.ToString() ?? string.Empty, out cur); } catch { }
                cur += delta;
                // clamp to reasonable range
                cur = Math.Max(-10000.0, Math.Min(100000.0, cur));
                st["Companion_target_alt"] = cur.ToString(CultureInfo.InvariantCulture);
                try { st.Save(); } catch { }
            }
            catch { }
        }

        private ToolStripMenuItem FindMenuItemByPath(string path)
        {
            // path is like "Parent>Child>Item"
            var parts = path.Split(new char[] {'>'}, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            ToolStripItemCollection col = Host.FDMenuMap.Items;
            ToolStripMenuItem current = null;
            foreach (var part in parts)
            {
                bool found = false;
                foreach (ToolStripItem it in col)
                {
                    if (it is ToolStripMenuItem m && m.Text == part)
                    {
                        current = m;
                        col = m.DropDownItems;
                        found = true;
                        break;
                    }
                }
                if (!found) return null;
            }
            return current;
        }

        private void ShowManageDialog()
        {
            var f = new Form() { Text = "KeyBinder - Manage Bindings", Size = new Size(600, 400), StartPosition = FormStartPosition.CenterParent };

            var list = new ListView() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Top, Height = 220 };
            list.Columns.Add("Key"); list.Columns.Add("Menu Path");
            try { var snapshot = new KeyValuePair<string, string>[0]; lock (bindingsLock) { snapshot = bindings.ToArray(); } foreach (var kv in snapshot) list.Items.Add(new ListViewItem(new string[] { kv.Key, kv.Value })); } catch { }

            var add = new Button() { Text = "Add", Left = 10, Top = 230, Width = 80 };
            var remove = new Button() { Text = "Remove", Left = 100, Top = 230, Width = 80 };
            var chkHold = new CheckBox() { Text = "Require map click while holding binding", Left = 200, Top = 232, Width = 300 };
            var close = new Button() { Text = "Close", Left = 500, Top = 330, Width = 80, DialogResult = DialogResult.OK };

            add.Click += (s, e) => { ShowAddDialog(list); };
            remove.Click += (s, e) => {
                if (list.SelectedItems.Count == 0) return;
                var k = list.SelectedItems[0].SubItems[0].Text;
                try { lock (bindingsLock) { bindings.Remove(k); } } catch { }
                list.Items.Remove(list.SelectedItems[0]);
                // remove hold setting as well
                try { var key = SanitizeKeyName(k); Settings.Instance["KeyBinder_hold_" + key] = null; Settings.Instance.Save(); } catch { }
                SaveBindings();
            };

            list.SelectedIndexChanged += (s,e) => {
                try {
                    if (list.SelectedItems.Count == 0) { chkHold.Checked = false; chkHold.Enabled = false; return; }
                    chkHold.Enabled = true;
                    var k = list.SelectedItems[0].SubItems[0].Text;
                    chkHold.Checked = IsHoldRequired(k);
                } catch { }
            };

            chkHold.CheckedChanged += (s,e) => {
                try {
                    if (list.SelectedItems.Count == 0) return;
                    var k = list.SelectedItems[0].SubItems[0].Text;
                    SetHoldRequired(k, chkHold.Checked);
                } catch { }
            };

            f.Controls.Add(list); f.Controls.Add(add); f.Controls.Add(remove); f.Controls.Add(close); f.Controls.Add(chkHold);
            f.ShowDialog(MainV2.instance);
            f.Dispose();
        }

        private void ShowAddDialog(ListView list)
        {
            var f = new Form() { Text = "Add Binding", Size = new Size(500, 260), StartPosition = FormStartPosition.CenterParent };
            var lblKey = new Label() { Text = "Press key combination:", Left = 10, Top = 10, Width = 200 };
            var keyBox = new TextBox() { Left = 10, Top = 30, Width = 200, ReadOnly = true };
            var capture = new Button() { Text = "Capture", Left = 220, Top = 30, Width = 80 };

                capture.Click += (s, e) => {
                    var capForm = new Form() { Text = "Press desired key combination", Size = new Size(420, 140), StartPosition = FormStartPosition.CenterParent, KeyPreview = true };
                    var info = new Label() { Text = "Press and hold the combination, then release all keys to capture...", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
                    capForm.Controls.Add(info);

                    var down = new HashSet<Keys>();
                    var mods = new HashSet<Keys>();
                    Keys lastNonMod = Keys.None;
                    bool sawAny = false;

                    Func<string> buildText = () => {
                        var parts = new List<string>();
                        if (mods.Any(k => k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey)) parts.Add("Ctrl");
                        if (mods.Any(k => k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu)) parts.Add("Alt");
                        if (mods.Any(k => k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey)) parts.Add("Shift");
                        // choose primary key
                        Keys primary = Keys.None;
                        if (lastNonMod != Keys.None) primary = lastNonMod;
                        else
                        {
                            var nonmods = down.Where(k => !(k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu || k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey)).ToArray();
                            if (nonmods.Length > 0) primary = nonmods[0];
                        }
                        if (primary != Keys.None) parts.Add(primary.ToString());
                        return string.Join("+", parts);
                    };

                    KeyEventHandler kd = null;
                    KeyEventHandler ku = null;
                    kd = (o, ke) => {
                        var k = ke.KeyCode;
                        if (!down.Contains(k)) down.Add(k);
                        // record modifiers separately so they survive until finalize
                        if (k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu || k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey)
                            mods.Add(k);
                        else
                            lastNonMod = k;
                        sawAny = true;
                        info.Text = buildText();
                    };
                    ku = (o, ke) => {
                        down.Remove(ke.KeyCode);
                        info.Text = buildText();
                        if (down.Count == 0 && sawAny)
                        {
                            // finalize using recorded mods and lastNonMod
                            keyBox.Text = buildText();
                            capForm.KeyDown -= kd;
                            capForm.KeyUp -= ku;
                            capForm.Close();
                        }
                    };

                    capForm.KeyDown += kd;
                    capForm.KeyUp += ku;
                    capForm.ShowDialog(MainV2.instance);
                    capForm.KeyDown -= kd;
                    capForm.KeyUp -= ku;
                    capForm.Dispose();
                };

            var lblMenu = new Label() { Text = "Select menu action:", Left = 10, Top = 70, Width = 200 };
            var menuCombo = new ComboBox() { Left = 10, Top = 90, Width = 460, DropDownStyle = ComboBoxStyle.DropDownList };
            // add KeyBinder-specific actions first
            menuCombo.Items.Add(KB_INC_TARGET_ALT);
            menuCombo.Items.Add(KB_DEC_TARGET_ALT);
            // populate with menu paths
            var items = EnumerateMenuPaths(Host.FDMenuMap.Items);
            foreach (var it in items) menuCombo.Items.Add(it);
            if (menuCombo.Items.Count > 0) menuCombo.SelectedIndex = 0;

            var ok = new Button() { Text = "OK", Left = 300, Top = 180, Width = 80, DialogResult = DialogResult.OK };
            var cancel = new Button() { Text = "Cancel", Left = 390, Top = 180, Width = 80, DialogResult = DialogResult.Cancel };

            ok.Click += (s, e) => {
                if (string.IsNullOrEmpty(keyBox.Text)) return;
                var sig = keyBox.Text.Replace(" ", "");
                var path = menuCombo.SelectedItem as string;
                if (!string.IsNullOrEmpty(sig) && !string.IsNullOrEmpty(path))
                {
                    try { lock (bindingsLock) { bindings[sig] = path; } } catch { }
                    list.Items.Add(new ListViewItem(new string[] { sig, path }));
                    // default: not require hold unless user changes in manage dialog
                    SetHoldRequired(sig, false);
                    SaveBindings();
                    f.Close();
                }
            };

            f.Controls.Add(lblKey); f.Controls.Add(keyBox); f.Controls.Add(capture);
            f.Controls.Add(lblMenu); f.Controls.Add(menuCombo); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.ShowDialog(MainV2.instance);
        }

        private List<string> EnumerateMenuPaths(ToolStripItemCollection items)
        {
            var res = new List<string>();
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripMenuItem m)
                {
                    if (m.DropDownItems.Count == 0)
                        res.Add(m.Text);
                    else
                    {
                        // leaf items and nested
                        EnumerateMenuPathsRecursive(m, m.Text, res);
                    }
                }
            }
            return res;
        }

        private void EnumerateMenuPathsRecursive(ToolStripMenuItem item, string prefix, List<string> res)
        {
            foreach (ToolStripItem it in item.DropDownItems)
            {
                if (it is ToolStripMenuItem m)
                {
                    string path = prefix + ">" + m.Text;
                    if (m.DropDownItems.Count == 0) res.Add(path);
                    else EnumerateMenuPathsRecursive(m, path, res);
                }
            }
        }

        private void LoadBindings()
        {
            try
            {
                var s = Settings.Instance.GetString("KeyBinder_bindings", "");
                if (string.IsNullOrEmpty(s)) return;
                try
                {
                    // try JSON format first (new, robust)
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(s);
                    if (dict != null)
                    {
                        lock (bindingsLock) { bindings = new Dictionary<string, string>(dict); }
                        return;
                    }
                }
                catch { /* fall back to legacy format */ }

                // legacy format: lines of "key|value"
                var lines = s.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                try { lock (bindingsLock) { bindings.Clear(); foreach (var ln in lines) { var parts = ln.Split(new[] { '|' }, 2); if (parts.Length == 2) bindings[parts[0]] = parts[1]; } } } catch { }
            }
            catch { }
        }

        private string SanitizeKeyName(string sig)
        {
            try { return sig.Replace("+", "_").Replace(" ", "_"); } catch { return sig; }
        }

        private bool IsHoldRequired(string sig)
        {
            try
            {
                var k = SanitizeKeyName(sig);
                var v = Settings.Instance["KeyBinder_hold_" + k];
                if (v == null) return false;
                var s = v.ToString();
                return s == "1" || s.ToLower() == "true";
            }
            catch { return false; }
        }

        private void SetHoldRequired(string sig, bool enabled)
        {
            try
            {
                var k = SanitizeKeyName(sig);
                Settings.Instance["KeyBinder_hold_" + k] = enabled ? "1" : "0";
                Settings.Instance.Save();
            }
            catch { }
        }

        private void SaveBindings()
        {
            try
            {
                // serialize to JSON for robustness
                Dictionary<string, string> snapshot = null;
                try { lock (bindingsLock) { snapshot = new Dictionary<string, string>(bindings); } } catch { snapshot = new Dictionary<string, string>(); }
                var json = JsonConvert.SerializeObject(snapshot);
                Settings.Instance["KeyBinder_bindings"] = json;
                Settings.Instance.Save();
            }
            catch { }
        }

        public override bool Exit()
        {
            try { if (_keyHandlersAttached && MainV2.instance != null) { MainV2.instance.KeyDown -= Instance_KeyDown; MainV2.instance.KeyUp -= Instance_KeyUp; _keyHandlersAttached = false; } } catch { }
            try { if (_mapHandlerAttached && FlightData.instance != null && FlightData.instance.gMapControl1 != null) { FlightData.instance.gMapControl1.MouseDown -= GMap_MouseDown; _mapHandlerAttached = false; } } catch { }
            try { if (_menuRoot != null && Host?.FDMenuMap != null) { Host.FDMenuMap.Items.Remove(_menuRoot); _menuRoot = null; } } catch { }
            return true;
        }
    }
}
