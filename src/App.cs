using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryBar
{
    static class Program
    {
        public const string Version = "1.0.0";
        public const string Repo = "https://github.com/cappucinudev/battery-bar";
        const string MutexName = "BatteryBar.Single";
        public const string ShowEvent = "BatteryBar.ShowSettings";
        public const string ExitEvent = "BatteryBar.Exit";

        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BatteryBar"); } }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "BatteryBar.exe"); } }
        public static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Battery Bar.lnk"); } }
        public static bool IsInstalledCopy { get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); } }

        [STAThread]
        static void Main(string[] args)
        {
            // BatteryBar.exe --dump file.txt : write what every source sees, then quit
            if (args.Length >= 2 && args[0] == "--dump")
            {
                var p = new Poller();
                p.PollAll(true);
                File.WriteAllText(args[1], p.Log);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool welcome = args.Contains("--welcome");
            Poller.Demo = args.Contains("--demo");
            if (!IsInstalledCopy && !args.Contains("--portable"))
            {
                var answer = MessageBox.Show(
                    "Install Battery Bar for this Windows user?\n\n" +
                    "Yes: install, start with Windows, add it to the Start menu.\n" +
                    "No: just run it from here this time.",
                    "Battery Bar " + Version, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes)
                {
                    try { Installer.Install(); }
                    catch (Exception e) { MessageBox.Show("Install failed: " + e.Message, "Battery Bar"); }
                    return;
                }
                welcome = true;
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) { Native.AllowSetForegroundWindow(-1); Signal(ShowEvent); return; }
                Application.Run(new AppContext(welcome));
            }
        }

        public static void Signal(string name)
        {
            try { using (var e = EventWaitHandle.OpenExisting(name)) e.Set(); } catch { }
        }

        public static bool Running()
        {
            try { using (Mutex.OpenExisting(MutexName)) return true; } catch { return false; }
        }
    }

    static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void Install()
        {
            Program.Signal(Program.ExitEvent); // stop an older running copy
            for (int i = 0; i < 50 && Program.Running(); i++) Thread.Sleep(100);
            Directory.CreateDirectory(Program.InstallDir);
            File.Copy(Application.ExecutablePath, Program.InstalledExe, true);
            SetStartup(true, Program.InstalledExe);
            MakeShortcut();
            Process.Start(Program.InstalledExe, "--welcome");
        }

        public static bool StartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("BatteryBar") != null;
        }

        public static void SetStartup(bool on, string exe)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("BatteryBar", "\"" + exe + "\"");
                else if (k.GetValue("BatteryBar") != null) k.DeleteValue("BatteryBar");
            }
        }

        static void MakeShortcut()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { Program.Shortcut });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Program.InstalledExe });
                lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "Battery levels on the taskbar" });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch { }
        }

        // Remove startup entry, shortcut, settings and (after exit) the installed files.
        public static void Uninstall()
        {
            SetStartup(false, null);
            try { File.Delete(Program.Shortcut); } catch { }
            try { Directory.Delete(Settings.Dir, true); } catch { }
            if (Directory.Exists(Program.InstallDir))
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Program.InstallDir + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
        }
    }

    class Settings
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryBar");
        static string FilePath { get { return Path.Combine(Dir, "settings.txt"); } }

        public int Interval = 60;      // seconds between checks
        public float FontSize = 12;    // px at 100% scaling
        public int Offset = 0;         // extra px to the left of the tray
        public bool PercentSign = true;
        public readonly HashSet<string> Hidden = new HashSet<string>();
        public readonly Dictionary<string, string> Kind = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>();

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 2) continue;
                    switch (p[0])
                    {
                        case "interval": s.Interval = Math.Max(10, int.Parse(p[1])); break;
                        case "font": s.FontSize = Math.Max(8, float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)); break;
                        case "offset": s.Offset = int.Parse(p[1]); break;
                        case "percent": s.PercentSign = p[1] == "1"; break;
                        case "hide": s.Hidden.Add(p[1]); break;
                        case "kind": if (p.Length > 2) s.Kind[p[1]] = p[2]; break;
                        case "name": if (p.Length > 2) s.Names[p[1]] = p[2]; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                lines.Add("interval\t" + Interval);
                lines.Add("font\t" + FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("offset\t" + Offset);
                lines.Add("percent\t" + (PercentSign ? "1" : "0"));
                foreach (var h in Hidden) lines.Add("hide\t" + h);
                foreach (var k in Kind) lines.Add("kind\t" + k.Key + "\t" + k.Value);
                foreach (var n in Names) lines.Add("name\t" + n.Key + "\t" + n.Value);
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }
    }

    class AppContext : ApplicationContext
    {
        public readonly Settings Settings = Settings.Load();
        public readonly Poller Poller = new Poller();
        public readonly Dictionary<string, Reading> Latest = new Dictionary<string, Reading>();
        readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
        readonly Control ui = new Control();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        Bar bar;
        SettingsForm form;
        bool polledOnce;

        public AppContext(bool welcome)
        {
            ui.CreateControl();
            IntPtr force = ui.Handle;

            menu.Items.Add("Settings", null, delegate { ShowSettings(); });
            menu.Items.Add("Refresh now", null, delegate { Refresh(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            timer.Interval = 1000;
            timer.Tick += delegate { KeepBar(); };
            timer.Start();
            KeepBar();

            var poll = new Thread(PollLoop);
            poll.IsBackground = true;
            poll.Start();

            var events = new Thread(WaitEvents);
            events.IsBackground = true;
            events.Start();

            if (welcome) ShowSettings();
        }

        public void Refresh() { wake.Set(); }

        void PollLoop()
        {
            while (true)
            {
                List<Reading> list;
                try { list = Poller.PollAll(false); }
                catch (Exception e) { list = new List<Reading>(); Poller.Log += "poll failed: " + e + "\r\n"; }
                try { Directory.CreateDirectory(Settings.Dir); File.WriteAllText(Path.Combine(Settings.Dir, "last-check.txt"), Poller.Log); } catch { }
                try { ui.BeginInvoke(new Action(() => Apply(list))); } catch { return; }
                wake.WaitOne(Settings.Interval * 1000);
            }
        }

        void WaitEvents()
        {
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEvent))
            using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEvent))
            {
                while (true)
                {
                    int i = WaitHandle.WaitAny(new WaitHandle[] { show, exit });
                    try
                    {
                        if (i == 0) ui.BeginInvoke(new Action(ShowSettings));
                        else { ui.BeginInvoke(new Action(ExitThread)); return; }
                    }
                    catch { return; }
                }
            }
        }

        void Apply(List<Reading> list)
        {
            polledOnce = true;
            bool newName = false;
            foreach (var r in list)
            {
                Latest[r.Key] = r;
                seen[r.Key] = DateTime.UtcNow;
                string old;
                if (!Settings.Names.TryGetValue(r.Key, out old) || old != r.Name) { Settings.Names[r.Key] = r.Name; newName = true; }
            }
            if (newName && !Poller.Demo) Settings.Save();
            try { Redraw(); }
            catch (Exception e) { try { File.AppendAllText(Path.Combine(Settings.Dir, "last-check.txt"), "redraw failed: " + e + "\r\n"); } catch { } }
            if (form != null && !form.IsDisposed) form.Fill();
        }

        public string KindOf(Reading r)
        {
            string k;
            return Settings.Kind.TryGetValue(r.Key, out k) ? k : r.Kind;
        }

        // Readings that are fresh (a missed check or two is tolerated) and not hidden.
        public List<Reading> Fresh()
        {
            // devices nap (mice sleep after a few idle minutes): keep the last value a while
            var keep = TimeSpan.FromSeconds(Math.Max(Settings.Interval * 2.5 + 10, 1800));
            return Latest.Values.Where(r => DateTime.UtcNow - seen[r.Key] < keep).ToList();
        }

        public void Redraw()
        {
            if (bar == null) return;
            var shown = new List<Reading>();
            foreach (var r in Fresh())
            {
                if (Settings.Hidden.Contains(r.Key)) continue;
                var c = new Reading { Key = r.Key, Name = r.Name, Pct = r.Pct, Charging = r.Charging, Source = r.Source, Kind = KindOf(r) };
                shown.Add(c);
            }
            shown.Sort((a, b) => Kinds.Order(a.Kind) != Kinds.Order(b.Kind) ? Kinds.Order(a.Kind) - Kinds.Order(b.Kind) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (!polledOnce && shown.Count == 0) shown = null;
            bar.SetItems(shown ?? new List<Reading>());
        }

        void KeepBar()
        {
            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
            if (tb == IntPtr.Zero) return;
            if (bar == null || bar.Taskbar != tb || !bar.Alive)
            {
                if (bar != null) bar.Destroy();
                try
                {
                    bar = new Bar(tb, Settings);
                    bar.LeftClick += delegate { ShowSettings(); };
                    bar.RightClick += delegate { Native.SetForegroundWindow(ui.Handle); menu.Show(Cursor.Position); };
                    Redraw();
                }
                catch { bar = null; return; }
            }
            bar.Place();
        }

        public void ShowSettings()
        {
            if (form == null || form.IsDisposed) form = new SettingsForm(this);
            form.Fill();
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            Native.SetForegroundWindow(form.Handle);
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            if (bar != null) bar.Destroy();
            if (form != null && !form.IsDisposed) form.Close();
            base.ExitThreadCore();
        }
    }
}
