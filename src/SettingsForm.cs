using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BatteryBar
{
    sealed class SettingsForm : Form
    {
        readonly AppContext app;
        readonly DataGridView grid = new DataGridView();
        readonly NumericUpDown interval = new NumericUpDown(), size = new NumericUpDown(), offset = new NumericUpDown();
        readonly CheckBox startup = new CheckBox(), percent = new CheckBox();
        readonly Label status = new Label();
        bool filling;

        public SettingsForm(AppContext app)
        {
            this.app = app;
            Text = "Battery Bar " + Program.Version;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 430);
            MinimumSize = new Size(520, 380);
            ShowIcon = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(new Label { AutoSize = true, Text = "Devices found on this PC. Untick to hide one, change Type to pick its icon.", Margin = new Padding(0, 0, 0, 6) });

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = SystemColors.Window;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Show", FillWeight = 10 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Device", ReadOnly = true, FillWeight = 40 });
            var type = new DataGridViewComboBoxColumn { HeaderText = "Type", FillWeight = 24, FlatStyle = FlatStyle.Flat };
            type.Items.AddRange(Kinds.Labels);
            grid.Columns.Add(type);
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Battery", ReadOnly = true, FillWeight = 22 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Via", ReadOnly = true, FillWeight = 14 });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += GridChanged;
            grid.DataError += delegate { };
            root.Controls.Add(grid);

            var opts = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            Num(opts, "Check every", interval, 10, 3600, app.Settings.Interval, "s");
            Num(opts, "Text size", size, 8, 30, (decimal)app.Settings.FontSize, "px");
            Num(opts, "Move left", offset, -2000, 2000, app.Settings.Offset, "px");
            percent.Text = "Show %"; percent.AutoSize = true; percent.Checked = app.Settings.PercentSign; percent.Margin = new Padding(8, 6, 8, 0);
            startup.Text = "Start with Windows"; startup.AutoSize = true; startup.Checked = Installer.StartupEnabled(); startup.Margin = new Padding(8, 6, 8, 0);
            opts.Controls.Add(percent);
            opts.Controls.Add(startup);
            interval.ValueChanged += OptionsChanged;
            size.ValueChanged += OptionsChanged;
            offset.ValueChanged += OptionsChanged;
            percent.CheckedChanged += OptionsChanged;
            startup.CheckedChanged += delegate
            {
                try { Installer.SetStartup(startup.Checked, Application.ExecutablePath); }
                catch (Exception e) { MessageBox.Show(e.Message, Text); }
            };
            root.Controls.Add(opts);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            Button("Refresh now", buttons, delegate { status.Text = "Checking..."; app.Refresh(); });
            Button("Copy diagnostics", buttons, delegate { Clipboard.SetText(app.Poller.Log); status.Text = "Copied. Paste it into a GitHub issue."; });
            Button("Help / updates", buttons, delegate { Process.Start(Program.Repo); });
            Button("Uninstall", buttons, delegate
            {
                if (MessageBox.Show("Remove Battery Bar from this PC?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Installer.Uninstall();
                app.ExitThread();
            });
            Button("Close", buttons, delegate { Close(); });
            status.AutoSize = true; status.Margin = new Padding(8, 8, 0, 0);
            buttons.Controls.Add(status);
            root.Controls.Add(buttons);
        }

        static void Num(FlowLayoutPanel p, string label, NumericUpDown n, int min, int max, decimal value, string unit)
        {
            p.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
            n.Minimum = min; n.Maximum = max; n.Value = Math.Max(min, Math.Min(max, value)); n.Width = 60;
            p.Controls.Add(n);
            p.Controls.Add(new Label { Text = unit, AutoSize = true, Margin = new Padding(0, 6, 10, 0) });
        }

        static void Button(string text, FlowLayoutPanel p, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 0, 4, 0) };
            b.Click += click;
            p.Controls.Add(b);
        }

        public void Fill()
        {
            filling = true;
            try
            {
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                var fresh = app.Fresh().ToDictionary(r => r.Key);
                var keys = app.Settings.Names.Keys.Union(fresh.Keys)
                    .OrderBy(k => fresh.ContainsKey(k) ? 0 : 1)
                    .ThenBy(k => app.Settings.Names.ContainsKey(k) ? app.Settings.Names[k] : k).ToList();
                // keep rows in place when the set did not change (so editing is not interrupted)
                bool same = grid.Rows.Count == keys.Count && keys.Select((k, i) => (string)grid.Rows[i].Tag == k).All(x => x);
                if (!same) grid.Rows.Clear();
                for (int i = 0; i < keys.Count; i++)
                {
                    string k = keys[i];
                    Reading r;
                    fresh.TryGetValue(k, out r);
                    string name = app.Settings.Names.ContainsKey(k) ? app.Settings.Names[k] : (r != null ? r.Name : k);
                    string kind;
                    if (!app.Settings.Kind.TryGetValue(k, out kind)) kind = r != null ? r.Kind : Kinds.Guess(name, "other");
                    object[] values = {
                        !app.Settings.Hidden.Contains(k), name, Kinds.Label(kind),
                        r == null ? "not connected" : r.Pct + "%" + (r.Charging ? " charging" : ""),
                        r == null ? "" : r.Source };
                    if (!same) { int row = grid.Rows.Add(values); grid.Rows[row].Tag = k; }
                    else for (int c = 0; c < values.Length; c++) if (!Equals(grid.Rows[i].Cells[c].Value, values[c])) grid.Rows[i].Cells[c].Value = values[c];
                    grid.Rows[i].DefaultCellStyle.ForeColor = r == null ? SystemColors.GrayText : SystemColors.ControlText;
                }
                status.Text = keys.Count == 0 ? "No devices yet. Turn them on, then Refresh now." : "";
            }
            finally { filling = false; }
        }

        void GridChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (filling || e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            string key = (string)row.Tag;
            if (e.ColumnIndex == 0)
            {
                if (true.Equals(row.Cells[0].Value)) app.Settings.Hidden.Remove(key);
                else app.Settings.Hidden.Add(key);
            }
            else if (e.ColumnIndex == 2)
                app.Settings.Kind[key] = Kinds.FromLabel((string)row.Cells[2].Value);
            else return;
            app.Settings.Save();
            app.Redraw();
        }

        void OptionsChanged(object sender, EventArgs e)
        {
            var s = app.Settings;
            bool intervalChanged = s.Interval != (int)interval.Value;
            s.Interval = (int)interval.Value;
            s.FontSize = (float)size.Value;
            s.Offset = (int)offset.Value;
            s.PercentSign = percent.Checked;
            s.Save();
            app.Redraw();
            if (intervalChanged) app.Refresh();
        }
    }
}
