using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryBar
{
    // The strip drawn inside the Windows taskbar, just left of the tray icons.
    // It is a transparent (per-pixel alpha) child window of Shell_TrayWnd, so it moves,
    // hides and stacks with the taskbar (hidden under full-screen games).
    sealed class Bar : NativeWindow
    {
        public readonly IntPtr Taskbar;
        public event EventHandler LeftClick, RightClick;

        readonly Settings settings;
        List<Reading> items = new List<Reading>();
        int width = 1, height = 1, lastX = int.MinValue, lastDpi;
        bool lastLight;
        DateTime lastZ = DateTime.MinValue;
        static string iconFont;

        public Bar(IntPtr taskbar, Settings s)
        {
            Taskbar = taskbar;
            settings = s;
            var cp = new CreateParams();
            cp.Caption = "BatteryBar";
            cp.Parent = taskbar;
            cp.Style = 0x40000000 | 0x10000000 | 0x04000000; // CHILD | VISIBLE | CLIPSIBLINGS
            cp.ExStyle = 0x00080000 | 0x08000000;             // LAYERED | NOACTIVATE
            cp.Width = 1; cp.Height = 1;
            CreateHandle(cp);
        }

        public bool Alive { get { return Handle != IntPtr.Zero && Native.IsWindow(Handle) && Native.IsWindow(Taskbar); } }

        public void SetItems(List<Reading> list) { items = list; Render(); }

        static bool LightTaskbar()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        static string IconFont()
        {
            if (iconFont == null)
            {
                iconFont = "Segoe MDL2 Assets";
                using (var fonts = new InstalledFontCollection())
                    foreach (var f in fonts.Families) if (f.Name == "Segoe Fluent Icons") iconFont = f.Name;
            }
            return iconFont;
        }

        float Scale()
        {
            uint dpi = Native.GetDpiForWindow(Taskbar);
            return (dpi == 0 ? 96 : dpi) / 96f;
        }

        public void Render()
        {
            if (Handle == IntPtr.Zero) return;
            Native.RECT tb;
            Native.GetWindowRect(Taskbar, out tb);
            float s = Scale();
            lastDpi = (int)(s * 96);
            lastLight = LightTaskbar();
            height = Math.Max(1, tb.Bottom - tb.Top);
            Color fg = lastLight ? Color.Black : Color.White;

            var shown = items;
            bool empty = shown.Count == 0;
            using (var textFont = new Font("Segoe UI", settings.FontSize * s, GraphicsUnit.Pixel))
            using (var glyphFont = new Font(IconFont(), settings.FontSize * 1.2f * s, GraphicsUnit.Pixel))
            using (var boltFont = new Font(IconFont(), settings.FontSize * 0.85f * s, GraphicsUnit.Pixel))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (var measureBmp = new Bitmap(1, 1))
            using (var mg = Graphics.FromImage(measureBmp))
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float pad = 6 * s, gap = 4 * s, itemGap = 12 * s;

                // measure
                var parts = new List<object[]>(); // glyph, bolt?, text
                if (empty) parts.Add(new object[] { "", false, "--" });
                foreach (var r in shown)
                    parts.Add(new object[] { Kinds.Glyph(r.Kind), r.Charging, r.Pct + (settings.PercentSign ? "%" : "") });
                float w = pad;
                var widths = new List<float[]>();
                foreach (var p in parts)
                {
                    float gw = mg.MeasureString((string)p[0], glyphFont, 1000, fmt).Width;
                    float bw = (bool)p[1] ? mg.MeasureString("", boltFont, 1000, fmt).Width : 0;
                    float tw = mg.MeasureString((string)p[2], textFont, 1000, fmt).Width;
                    widths.Add(new[] { gw, bw, tw });
                    w += gw + gap + bw + tw + itemGap;
                }
                w = w - itemGap + pad;
                width = Math.Max(1, (int)Math.Ceiling(w));

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                using (var brush = new SolidBrush(fg))
                {
                    g.Clear(Color.FromArgb(1, 0, 0, 0)); // nearly invisible, but still catches clicks
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float x = pad;
                    float textH = textFont.GetHeight(g), glyphH = glyphFont.GetHeight(g), boltH = boltFont.GetHeight(g);
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var p = parts[i]; var wd = widths[i];
                        g.DrawString((string)p[0], glyphFont, brush, x, (height - glyphH) / 2f + s, fmt);
                        x += wd[0] + gap;
                        if ((bool)p[1])
                        {
                            g.DrawString("", boltFont, brush, x, (height - boltH) / 2f + s, fmt);
                            x += wd[1];
                        }
                        g.DrawString((string)p[2], textFont, brush, x, (height - textH) / 2f, fmt);
                        x += wd[2] + itemGap;
                    }
                    Push(bmp);
                }
            }
            lastX = int.MinValue;
            Place();
        }

        void Push(Bitmap bmp)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(mem, hb);
            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2);
            Native.SelectObject(mem, old);
            Native.DeleteObject(hb);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        // Keep the strip just left of the tray, on top of the taskbar's own content.
        public void Place()
        {
            if (Handle == IntPtr.Zero) return;
            if ((int)(Scale() * 96) != lastDpi || LightTaskbar() != lastLight) { Render(); return; }
            Native.RECT tb, n;
            Native.GetWindowRect(Taskbar, out tb);
            IntPtr notify = Native.FindWindowEx(Taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            int right = notify != IntPtr.Zero && Native.GetWindowRect(notify, out n) && n.Right > n.Left
                ? n.Left - tb.Left
                : (tb.Right - tb.Left) - (int)(300 * Scale());
            int x = right - width - (int)(settings.Offset * Scale());
            int h = tb.Bottom - tb.Top;
            bool moved = x != lastX || h != height;
            if (moved || DateTime.UtcNow - lastZ > TimeSpan.FromSeconds(3))
            {
                // HWND_TOP | NOACTIVATE | SHOWWINDOW (+ NOSIZE when only re-stacking)
                Native.SetWindowPos(Handle, IntPtr.Zero, x, 0, width, height, 0x0010 | 0x0040 | (moved ? 0u : 0x0001u));
                lastX = x; lastZ = DateTime.UtcNow;
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x0021: m.Result = (IntPtr)3; return; // WM_MOUSEACTIVATE -> MA_NOACTIVATE
                case 0x0202: if (LeftClick != null) LeftClick(this, EventArgs.Empty); return;
                case 0x0205: if (RightClick != null) RightClick(this, EventArgs.Empty); return;
            }
            base.WndProc(ref m);
        }

        public void Destroy() { try { if (Handle != IntPtr.Zero) DestroyHandle(); } catch { } }
    }
}
