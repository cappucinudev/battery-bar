using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryBar
{
    // Windows 11 style panel that opens above the taskbar when the battery strip is clicked.
    // Two pages: devices (battery bars, show/hide, icon type) and settings.
    sealed class Flyout : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        static readonly int[] Intervals = { 15, 30, 60, 120, 300, 600 };

        class Hit { public RectangleF R; public Action Act; }
        class Row { public string Key, Name, Kind; public Reading R; public bool Hidden; }
        class Theme { public Color Bg, Hover, Card, Text, Sub, Track, Fill, Line; }

        readonly AppContext app;
        readonly List<Hit> hits = new List<Hit>();
        readonly Timer toastTimer = new Timer();
        bool settingsPage;
        float s = 1;
        Point mouse = new Point(-1, -1);
        Theme t;
        string toast;
        Font fTitle, fName, fPct, fSmall, fGlyph, fGlyphSmall;
        string glyphFamily;
        public DateTime HiddenAt = DateTime.MinValue;

        public Flyout(AppContext app)
        {
            this.app = app;
            Text = "Battery Bar";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            toastTimer.Interval = 2500;
            toastTimer.Tick += delegate { toastTimer.Stop(); toast = null; Invalidate(); };
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // TOOLWINDOW: no Alt+Tab entry
        }

        // ---------- open / close ----------

        public void Open(bool settings)
        {
            settingsPage = settings;
            bool light = Bar.LightTaskbar();
            t = light
                ? new Theme { Bg = C(0xF9F9F9), Hover = C(0xEAEAEA), Card = C(0xEFEFEF), Text = C(0x1A1A1A), Sub = C(0x5F5F5F), Track = C(0xD6D6D6), Fill = C(0x1A1A1A), Line = C(0xDDDDDD) }
                : new Theme { Bg = C(0x2B2B2B), Hover = C(0x3A3A3A), Card = C(0x353535), Text = C(0xFFFFFF), Sub = C(0xA8A8A8), Track = C(0x4D4D4D), Fill = C(0xFFFFFF), Line = C(0x3F3F3F) };
            float ns = app.Scale();
            if (fTitle == null || ns != s) { s = ns; MakeFonts(); }
            if (!IsHandleCreated) CreateHandle();
            int dark = light ? 0 : 1, round = 2;
            DwmSetWindowAttribute(Handle, 20, ref dark, 4);  // dark title/border
            DwmSetWindowAttribute(Handle, 33, ref round, 4); // rounded corners + shadow
            BackColor = t.Bg;
            Relayout();
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Poller.Demo) return; // keeps it open for screenshots
            Hide();
            HiddenAt = DateTime.UtcNow;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            base.OnKeyDown(e);
        }

        public void Refill() { if (Visible) { Relayout(); Invalidate(); } }

        static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        void MakeFonts()
        {
            foreach (var f in new[] { fTitle, fName, fPct, fSmall, fGlyph, fGlyphSmall }) if (f != null) f.Dispose();
            glyphFamily = Bar.IconFont();
            fTitle = new Font("Segoe UI Semibold", 15 * s, GraphicsUnit.Pixel);
            fName = new Font("Segoe UI", 14 * s, GraphicsUnit.Pixel);
            fPct = new Font("Segoe UI Semibold", 14 * s, GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel);
            fGlyph = new Font(glyphFamily, 20 * s, GraphicsUnit.Pixel);
            fGlyphSmall = new Font(glyphFamily, 14 * s, GraphicsUnit.Pixel);
        }

        List<Row> Rows()
        {
            var fresh = app.Fresh().ToDictionary(r => r.Key);
            var keys = app.Settings.Names.Keys.Union(fresh.Keys).ToList();
            var rows = new List<Row>();
            foreach (var k in keys)
            {
                var row = new Row { Key = k };
                fresh.TryGetValue(k, out row.R);
                row.Name = app.Settings.Names.ContainsKey(k) ? app.Settings.Names[k] : (row.R != null ? row.R.Name : k);
                if (!app.Settings.Kind.TryGetValue(k, out row.Kind)) row.Kind = row.R != null ? row.R.Kind : Kinds.Guess(row.Name, "other");
                row.Hidden = app.Settings.Hidden.Contains(k);
                rows.Add(row);
            }
            return rows.OrderBy(r => r.R == null ? 1 : 0).ThenBy(r => Kinds.Order(r.Kind)).ThenBy(r => r.Name).ToList();
        }

        // ---------- size + position ----------

        const float W = 360, Pad = 16, Header = 56, RowH = 68, SetH = 46, Footer = 40;

        void Relayout()
        {
            float h;
            if (settingsPage) h = Header + SetH * 5 + 12 + 40 + 8 + 40 + Pad;
            else { int n = Rows().Count; h = Header + (n == 0 ? 92 : n * RowH) + Footer; }
            int w = (int)(W * s), hh = (int)(h * s);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Native.RECT br;
            int cx = app.BarRect(out br) ? (br.Left + br.Right) / 2 : wa.Right - w / 2;
            var scr = Screen.FromPoint(new Point(cx, app.BarRect(out br) ? br.Top : wa.Bottom - 1));
            wa = scr.WorkingArea;
            int gap = (int)(12 * s);
            int x = Math.Max(wa.Left + gap, Math.Min(wa.Right - w - gap, cx - w / 2));
            int y = wa.Bottom - hh - gap;
            if (wa.Bottom == scr.Bounds.Bottom && app.BarRect(out br)) y = br.Top - hh - gap; // auto-hide taskbar
            Bounds = new Rectangle(x, y, w, hh);
        }

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            if (t == null) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(t.Bg);
            hits.Clear();
            try { if (settingsPage) PaintSettings(g); else PaintDevices(g); }
            catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "last-check.txt"), "flyout paint failed: " + ex + Environment.NewLine); } catch { } }
        }

        float X(float v) { return v * s; }

        RectangleF Rf(float x, float y, float w, float h) { return new RectangleF(x * s, y * s, w * s, h * s); }

        // Registers a clickable area and paints its hover background.
        bool Button(Graphics g, RectangleF r, Action act, float radius, bool alwaysFill)
        {
            hits.Add(new Hit { R = r, Act = act });
            bool over = r.Contains(mouse);
            if (over || alwaysFill)
                using (var b = new SolidBrush(over ? t.Hover : t.Card)) Fill(g, b, r, radius * s);
            return over;
        }

        static void Fill(Graphics g, Brush b, RectangleF r, float radius)
        {
            if (radius <= 0.5f || r.Width < radius * 2 || r.Height < radius * 2) { g.FillRectangle(b, r); return; }
            using (var p = new GraphicsPath())
            {
                float d = radius * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        void Draw(Graphics g, string text, Font f, Color c, RectangleF r, StringAlignment h, StringAlignment v)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Alignment = h; sf.LineAlignment = v; sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(text, f, b, r, sf);
            }
        }

        void GlyphButton(Graphics g, RectangleF r, string glyph, Action act)
        {
            Button(g, r, act, 6, false);
            Draw(g, glyph, fGlyphSmall, t.Text, r, StringAlignment.Center, StringAlignment.Center);
        }

        void PaintDevices(Graphics g)
        {
            Draw(g, "Battery", fTitle, t.Text, Rf(Pad, 0, 200, Header), StringAlignment.Near, StringAlignment.Center);
            GlyphButton(g, Rf(W - Pad - 32, 12, 32, 32), "", () => { settingsPage = true; });          // settings
            GlyphButton(g, Rf(W - Pad - 68, 12, 32, 32), "", () => { app.Refresh(); ShowToast("Checking..."); }); // refresh

            var rows = Rows();
            float y = Header;
            if (rows.Count == 0)
            {
                Draw(g, "No devices yet.\nTurn on your headphones, mouse or keyboard.", fSmall, t.Sub, Rf(Pad, y, W - 2 * Pad, 80), StringAlignment.Center, StringAlignment.Center);
                y += 92;
            }
            foreach (var row in rows)
            {
                PaintRow(g, row, y);
                y += RowH;
            }
            using (var p = new Pen(t.Line, Math.Max(1, s))) g.DrawLine(p, X(0), X(y), X(W), X(y));
            string foot = toast ?? (app.LastPoll == DateTime.MinValue ? "Checking devices..." : "Updated " + app.LastPoll.ToString("t") + "  ·  click an icon to change it");
            Draw(g, foot, fSmall, t.Sub, Rf(Pad, y, W - 2 * Pad, Footer), StringAlignment.Near, StringAlignment.Center);
        }

        void PaintRow(Graphics g, Row row, float y)
        {
            bool on = row.R != null;
            Color main = on && !row.Hidden ? t.Text : t.Sub;

            // icon tile: click cycles the type
            var tile = Rf(Pad, y + 14, 40, 40);
            string key = row.Key, kind = row.Kind;
            Button(g, tile, () =>
            {
                int i = Array.IndexOf(Kinds.All, kind);
                app.Settings.Kind[key] = Kinds.All[(i + 1) % Kinds.All.Length];
                app.Settings.Save();
                app.Redraw();
                ShowToast("Icon: " + Kinds.Label(app.Settings.Kind[key]));
            }, 8, true);
            Draw(g, Kinds.Glyph(row.Kind), fGlyph, main, tile, StringAlignment.Center, StringAlignment.Center);

            float x0 = Pad + 40 + 12, right = W - Pad - 32 - 10;
            string pct = on ? row.R.Pct + "%" : "";
            Draw(g, row.Name, fName, main, Rf(x0, y + 10, right - x0 - 48, 20), StringAlignment.Near, StringAlignment.Center);
            Draw(g, pct, fPct, main, Rf(right - 48, y + 10, 48, 20), StringAlignment.Far, StringAlignment.Center);

            if (on)
            {
                var track = Rf(x0, y + 35, right - x0, 6);
                using (var b = new SolidBrush(t.Track)) Fill(g, b, track, 3 * s);
                var fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * Math.Min(100, row.R.Pct) / 100f), track.Height);
                using (var b = new SolidBrush(row.Hidden ? t.Sub : t.Fill)) Fill(g, b, fill, 3 * s);
            }
            string sub = !on ? "Not connected" : row.R.Source + (row.R.Charging ? "  ·  Charging" : "") + (row.R.Pct <= 15 && !row.R.Charging ? "  ·  Low" : "");
            if (row.Hidden) sub += "  ·  Hidden on taskbar";
            Draw(g, sub, fSmall, t.Sub, Rf(x0, y + 44, right - x0, 18), StringAlignment.Near, StringAlignment.Center);

            // eye: show / hide on the taskbar
            bool hidden = row.Hidden;
            GlyphButton(g, Rf(W - Pad - 32, y + 18, 32, 32), hidden ? "" : "", () =>
            {
                if (hidden) app.Settings.Hidden.Remove(key); else app.Settings.Hidden.Add(key);
                app.Settings.Save();
                app.Redraw();
            });
        }

        void PaintSettings(Graphics g)
        {
            GlyphButton(g, Rf(Pad - 6, 12, 32, 32), "", () => { settingsPage = false; });
            Draw(g, "Settings", fTitle, t.Text, Rf(Pad + 32, 0, 200, Header), StringAlignment.Near, StringAlignment.Center);
            var S = app.Settings;
            float y = Header;

            int ii = Array.IndexOf(Intervals, S.Interval);
            Stepper(g, y, "Check every", S.Interval < 60 ? S.Interval + " sec" : S.Interval / 60 + " min",
                () => { S.Interval = Intervals[Math.Max(0, (ii < 0 ? 2 : ii) - 1)]; app.Refresh(); },
                () => { S.Interval = Intervals[Math.Min(Intervals.Length - 1, (ii < 0 ? 2 : ii) + 1)]; app.Refresh(); });
            y += SetH;
            Stepper(g, y, "Text size", S.FontSize + " px", () => { S.FontSize = Math.Max(8, S.FontSize - 1); }, () => { S.FontSize = Math.Min(30, S.FontSize + 1); });
            y += SetH;
            Stepper(g, y, "Move left", S.Offset + " px", () => { S.Offset -= 10; }, () => { S.Offset += 10; });
            y += SetH;
            Toggle(g, y, "Show % sign", S.PercentSign, () => { S.PercentSign = !S.PercentSign; });
            y += SetH;
            bool startup = Installer.StartupEnabled();
            Toggle(g, y, "Start with Windows", startup, () => Installer.SetStartup(!startup, Application.ExecutablePath));
            y += SetH + 12;

            float bw = (W - 2 * Pad - 16) / 3;
            Pill(g, Rf(Pad, y, bw, 36), "Copy diagnostics", () => { Clipboard.SetText(app.Poller.Log); ShowToast("Copied"); });
            Pill(g, Rf(Pad + bw + 8, y, bw, 36), "Help", () => Process.Start(Program.Repo));
            Pill(g, Rf(Pad + 2 * (bw + 8), y, bw, 36), "Uninstall", () =>
            {
                if (MessageBox.Show("Remove Battery Bar from this PC?", "Battery Bar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Installer.Uninstall();
                app.ExitThread();
            });
            y += 48;
            Pill(g, Rf(Pad, y, W - 2 * Pad, 36), toast ?? ("Quit Battery Bar  ·  v" + Program.Version), () => app.ExitThread());
        }

        void Label(Graphics g, float y, string label)
        {
            Draw(g, label, fName, t.Text, Rf(Pad, y, 200, SetH), StringAlignment.Near, StringAlignment.Center);
        }

        void Stepper(Graphics g, float y, string label, string value, Action minus, Action plus)
        {
            Label(g, y, label);
            GlyphButton(g, Rf(W - Pad - 32, y + 7, 32, 32), "", () => { plus(); app.Settings.Save(); app.Redraw(); });
            Draw(g, value, fName, t.Text, Rf(W - Pad - 32 - 72, y, 72, SetH), StringAlignment.Center, StringAlignment.Center);
            GlyphButton(g, Rf(W - Pad - 32 - 72 - 32, y + 7, 32, 32), "", () => { minus(); app.Settings.Save(); app.Redraw(); });
        }

        void Toggle(Graphics g, float y, string label, bool on, Action flip)
        {
            Label(g, y, label);
            var r = Rf(W - Pad - 44, y + 13, 44, 20);
            var hit = Rf(Pad, y + 4, W - 2 * Pad, SetH - 8);
            hits.Add(new Hit { R = hit, Act = () => { flip(); app.Settings.Save(); app.Redraw(); } });
            using (var b = new SolidBrush(on ? t.Fill : t.Bg)) Fill(g, b, r, r.Height / 2);
            using (var p = new Pen(on ? t.Fill : t.Sub, Math.Max(1, s)))
            using (var path = new GraphicsPath())
            {
                float d = r.Height;
                path.AddArc(r.X, r.Y, d, d, 90, 180); path.AddArc(r.Right - d, r.Y, d, d, 270, 180); path.CloseFigure();
                g.DrawPath(p, path);
            }
            float k = 12 * s, kx = on ? r.Right - k - 4 * s : r.X + 4 * s;
            using (var b = new SolidBrush(on ? t.Bg : t.Sub)) g.FillEllipse(b, kx, r.Y + (r.Height - k) / 2, k, k);
        }

        void Pill(Graphics g, RectangleF r, string text, Action act)
        {
            Button(g, r, act, 6, true);
            Draw(g, text, fSmall, t.Text, r, StringAlignment.Center, StringAlignment.Center);
        }

        void ShowToast(string text) { toast = text; toastTimer.Stop(); toastTimer.Start(); Invalidate(); }

        // ---------- input ----------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var before = hits.FirstOrDefault(h => h.R.Contains(mouse));
            mouse = e.Location;
            var now = hits.FirstOrDefault(h => h.R.Contains(mouse));
            Cursor = now != null ? Cursors.Hand : Cursors.Default;
            if (before != now) Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { mouse = new Point(-1, -1); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            var h = hits.LastOrDefault(x => x.R.Contains(e.Location));
            if (h == null) return;
            try { h.Act(); }
            catch (Exception ex) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "last-check.txt"), "flyout action failed: " + ex + Environment.NewLine); } catch { } }
            if (IsDisposed || !Visible) return;
            Relayout();
            Invalidate();
        }
    }
}
