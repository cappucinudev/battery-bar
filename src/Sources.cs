using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    // One battery reading. Key is stable across runs so settings can refer to it.
    class Reading
    {
        public string Key, Name, Kind, Source;
        public int Pct;
        public bool Charging;
    }

    static class Kinds
    {
        public static readonly string[] All = { "headset", "mouse", "keyboard", "controller", "other" };
        public static readonly string[] Labels = { "Headphones", "Mouse", "Keyboard", "Controller", "Other" };

        public static int Order(string kind) { int i = Array.IndexOf(All, kind); return i < 0 ? All.Length : i; }
        public static string Label(string kind) { int i = Array.IndexOf(All, kind); return i < 0 ? "Other" : Labels[i]; }
        public static string FromLabel(string label) { int i = Array.IndexOf(Labels, label); return i < 0 ? "other" : All[i]; }

        // Segoe Fluent Icons / Segoe MDL2 Assets glyphs
        public static string Glyph(string kind)
        {
            switch (kind)
            {
                case "headset": return "";
                case "mouse": return "";
                case "keyboard": return "";
                case "controller": return "";
                default: return "";
            }
        }

        static readonly string[] HeadsetWords = { "headset", "headphone", "earbud", "buds", "airpods", "earphone", "kraken", "blackshark", "barracuda", "nari", "hammerhead", "arctis", "cloud", "void", "virtuoso", "wh-", "wf-", "astro", "quietcomfort", "bose", "beats", "jabra", "soundcore", "g435", "g535", "g733", "g935", "pro x" };
        static readonly string[] MouseWords = { "mouse", "viper", "deathadder", "basilisk", "naga", "orochi", "mamba", "cobra", "pro click", "atheris", "lancehead", "superlight", "g502", "g703", "g903", "g305", "g304", "mx master", "mx anywhere", "lift", "trackball" };
        static readonly string[] KeyboardWords = { "keyboard", "keys", "blackwidow", "huntsman", "deathstalker", "ornata", "cynosa", "g915", "g913", "g715", "mx mechanical", "k380", "k780" };
        static readonly string[] PadWords = { "controller", "gamepad", "xbox", "dualsense", "dualshock", "joy-con", "pro controller", "wolverine" };

        // Guess a kind from a device name; fall back when nothing matches.
        public static string Guess(string name, string fallback)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (Any(n, MouseWords)) return "mouse";
            if (Any(n, KeyboardWords)) return "keyboard";
            if (Any(n, PadWords)) return "controller";
            if (Any(n, HeadsetWords)) return "headset";
            return fallback;
        }

        static bool Any(string n, string[] words)
        {
            foreach (string w in words) if (n.Contains(w)) return true;
            return false;
        }
    }

    static class Json
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static Dictionary<string, object> Parse(string s)
        {
            try { return Ser.DeserializeObject(s) as Dictionary<string, object>; } catch { return null; }
        }

        public static object Get(object o, string path)
        {
            foreach (string part in path.Split('.'))
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(part, out o)) return null;
            }
            return o;
        }

        public static string Str(object o) { return o == null ? null : o.ToString(); }
        public static bool Bool(object o) { return o is bool && (bool)o; }
    }

    // ---------------- Logitech G HUB (local websocket the G HUB app runs) ----------------
    class GHubSource
    {
        public bool Available;
        public string Note = "";

        public void Poll(List<Reading> outList)
        {
            Available = false;
            Note = "not running";
            var ws = new ClientWebSocket();
            try
            {
                ws.Options.AddSubProtocol("json");
                ws.Options.SetRequestHeader("Origin", "file://");
                try
                {
                    if (!ws.ConnectAsync(new Uri("ws://127.0.0.1:9010"), CancellationToken.None).Wait(2000)) return;
                }
                catch { return; }
                if (ws.State != WebSocketState.Open) return;
                Available = true;

                var listMsg = Request(ws, "/devices/list");
                var infos = Json.Get(listMsg, "payload.deviceInfos") as object[];
                Note = listMsg == null ? "no reply to /devices/list" : infos == null ? "no deviceInfos" : infos.Length + " device(s) listed";
                if (infos == null) return;
                foreach (object o in infos)
                {
                    if (!Json.Bool(Json.Get(o, "capabilities.hasBatteryStatus"))) continue;
                    var st = Request(ws, "/battery/" + Json.Str(Json.Get(o, "id")) + "/state");
                    Note += "; " + Json.Str(Json.Get(o, "id")) + " battery: " + (st == null ? "no reply" : Json.Str(Json.Get(st, "result.code")) + " " + Json.Str(Json.Get(st, "result.what")));
                    if (Json.Str(Json.Get(st, "result.code")) != "SUCCESS") continue;
                    object pct = Json.Get(st, "payload.percentage");
                    if (pct == null) continue;
                    var r = new Reading();
                    r.Name = Json.Str(Json.Get(o, "extendedDisplayName")) ?? Json.Str(Json.Get(o, "displayName")) ?? "Logitech device";
                    r.Key = "logi:" + r.Name;
                    string type = (Json.Str(Json.Get(o, "deviceType")) ?? "").ToUpperInvariant();
                    string fallback = type == "MOUSE" ? "mouse" : type == "KEYBOARD" ? "keyboard" : type.Contains("HEAD") ? "headset" : "other";
                    r.Kind = fallback != "other" ? fallback : Kinds.Guess(r.Name, "other");
                    r.Pct = Math.Max(0, Math.Min(100, Convert.ToInt32(pct)));
                    r.Charging = Json.Bool(Json.Get(st, "payload.charging"));
                    r.Source = "G HUB";
                    outList.Add(r);
                }
            }
            finally
            {
                try { if (ws.State == WebSocketState.Open) ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(500); } catch { }
                ws.Dispose();
            }
        }

        static Dictionary<string, object> Request(ClientWebSocket ws, string path)
        {
            var b = Encoding.UTF8.GetBytes("{\"msgId\":\"bb\",\"verb\":\"GET\",\"path\":\"" + path + "\"}");
            if (!ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait(2000)) return null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            var buf = new byte[65536];
            while (DateTime.UtcNow < deadline)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    var t = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (!t.Wait(3000)) return null;
                    res = t.Result;
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);
                var msg = Json.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                if (Json.Str(Json.Get(msg, "path")) == path) return msg;
            }
            return null;
        }
    }

    // ---------------- Logitech HID++ 2.0 (receivers, USB, Bluetooth) when G HUB is not running ----------------
    class HidppSource
    {
        const byte Swid = 0x0B;

        class Dev { public string Name, Kind; public ushort Feature; public byte Index; }
        readonly Dictionary<string, Dev> found = new Dictionary<string, Dev>();
        readonly Dictionary<string, DateTime> absent = new Dictionary<string, DateTime>();

        public void Poll(List<Reading> outList)
        {
            foreach (var d in Native.HidDevices(v => v == 0x046D))
            {
                bool longReport = (d.UsagePage == 0xFF00 && d.Usage == 0x0002) || (d.UsagePage == 0xFF43 && d.Usage == 0x0202);
                if (!longReport || d.OutLen != 20) continue;
                using (var h = Native.Open(d.Path, Native.ReadWrite, true))
                {
                    if (h.IsInvalid) continue;
                    using (var ch = new HidChannel(h, d.InLen > 0 ? d.InLen : 20))
                    {
                        foreach (byte idx in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                        {
                            string key = d.Path + "|" + idx;
                            DateTime until;
                            if (absent.TryGetValue(key, out until) && DateTime.UtcNow < until) continue;
                            Dev dev;
                            if (!found.TryGetValue(key, out dev))
                            {
                                dev = Probe(ch, idx);
                                if (dev == null) { absent[key] = DateTime.UtcNow.AddMinutes(5); continue; }
                                found[key] = dev;
                            }
                            if (dev.Index == 0) continue; // no battery feature
                            var r = ReadBattery(ch, idx, dev);
                            if (r == null) continue; // asleep or off
                            r.Name = dev.Name ?? ("Logitech device " + d.Pid.ToString("X4"));
                            r.Key = "logi:" + r.Name;
                            r.Kind = dev.Kind;
                            r.Source = "Logitech";
                            outList.Add(r);
                        }
                    }
                }
            }
        }

        static Dev Probe(HidChannel ch, byte idx)
        {
            int nameFeat = FeatureIndex(ch, idx, 0x0005);
            if (nameFeat < 0) return null; // nothing answers on this index
            var dev = new Dev();
            foreach (ushort f in new ushort[] { 0x1004, 0x1000, 0x1001 })
            {
                int i = FeatureIndex(ch, idx, f);
                if (i > 0) { dev.Feature = f; dev.Index = (byte)i; break; }
            }
            string fallbackKind = "other";
            if (nameFeat > 0)
            {
                var r = Request(ch, idx, (byte)nameFeat, 0);
                if (r != null)
                {
                    int len = r[4];
                    var sb = new StringBuilder();
                    while (sb.Length < len)
                    {
                        var p = Request(ch, idx, (byte)nameFeat, 1, (byte)sb.Length);
                        if (p == null) break;
                        int before = sb.Length;
                        for (int i = 4; i < 20 && sb.Length < len; i++) if (p[i] != 0) sb.Append((char)p[i]);
                        if (sb.Length == before) break;
                    }
                    if (sb.Length > 0) dev.Name = sb.ToString().Trim();
                }
                var t = Request(ch, idx, (byte)nameFeat, 2);
                if (t != null)
                {
                    switch (t[4])
                    {
                        case 0: case 2: fallbackKind = "keyboard"; break;
                        case 3: case 4: case 5: fallbackKind = "mouse"; break;
                        case 8: fallbackKind = "headset"; break;
                        case 11: case 12: fallbackKind = "controller"; break;
                    }
                }
            }
            dev.Kind = fallbackKind != "other" ? fallbackKind : Kinds.Guess(dev.Name, "other");
            return dev;
        }

        static Reading ReadBattery(HidChannel ch, byte idx, Dev dev)
        {
            var r = new Reading();
            byte[] p;
            switch (dev.Feature)
            {
                case 0x1004: // unified battery: get_status
                    p = Request(ch, idx, dev.Index, 1);
                    if (p == null) return null;
                    r.Pct = p[4];
                    r.Charging = p[6] >= 1 && p[6] <= 3;
                    break;
                case 0x1000: // battery level status
                    p = Request(ch, idx, dev.Index, 0);
                    if (p == null) return null;
                    r.Pct = p[4];
                    r.Charging = p[6] >= 1 && p[6] <= 4;
                    break;
                default: // 0x1001 battery voltage
                    p = Request(ch, idx, dev.Index, 0);
                    if (p == null) return null;
                    r.Pct = VoltageToPercent((p[4] << 8) | p[5]);
                    r.Charging = (p[6] & 0x80) != 0;
                    break;
            }
            if (r.Pct <= 0 || r.Pct > 100) return null;
            return r;
        }

        // Typical Li-ion discharge curve used by Logitech mice that only report voltage.
        static readonly int[,] Curve = { { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3869, 60 }, { 3830, 50 }, { 3802, 40 }, { 3778, 30 }, { 3755, 20 }, { 3733, 10 }, { 3684, 5 }, { 3500, 1 } };

        static int VoltageToPercent(int mv)
        {
            if (mv >= Curve[0, 0]) return 100;
            for (int i = 1; i < Curve.GetLength(0); i++)
            {
                if (mv >= Curve[i, 0])
                {
                    double f = (double)(mv - Curve[i, 0]) / (Curve[i - 1, 0] - Curve[i, 0]);
                    return (int)Math.Round(Curve[i, 1] + f * (Curve[i - 1, 1] - Curve[i, 1]));
                }
            }
            return 1;
        }

        static int FeatureIndex(HidChannel ch, byte idx, ushort feature)
        {
            var r = Request(ch, idx, 0x00, 0, (byte)(feature >> 8), (byte)feature);
            return r == null ? -1 : r[4];
        }

        static byte[] Request(HidChannel ch, byte idx, byte feat, int fn, params byte[] args)
        {
            var b = new byte[20];
            b[0] = 0x11; b[1] = idx; b[2] = feat; b[3] = (byte)((fn << 4) | Swid);
            if (args != null) Array.Copy(args, 0, b, 4, Math.Min(args.Length, 16));
            try { ch.Write(b); } catch { return null; }
            var until = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < until)
            {
                byte[] r;
                try { r = ch.Read((int)(until - DateTime.UtcNow).TotalMilliseconds); } catch { return null; }
                if (r == null) return null;
                if (r.Length < 7 || r[1] != idx) continue;
                if (r[2] == 0x8F && r[3] == feat) return null;                 // HID++ 1.0 error
                if (r[2] == 0xFF && r[3] == feat && r[4] == b[3]) return null; // HID++ 2.0 error
                if (r[2] == feat && r[3] == b[3] && r.Length >= 20) return r;
            }
            return null;
        }
    }

    // ---------------- Razer (Synapse not needed) ----------------
    class RazerSource
    {
        readonly Dictionary<string, byte> tids = new Dictionary<string, byte>();

        // Dongles whose USB product string is generic ("Hid Interface", "USB Receiver").
        static readonly Dictionary<ushort, string> KnownNames = new Dictionary<ushort, string> {
            { 0x0528, "Razer BlackShark V2 Pro" }, { 0x0555, "Razer BlackShark V2 Pro" }, { 0x0556, "Razer BlackShark V2 Pro" },
            { 0x0527, "Razer Kraken Ultimate" }, { 0x0568, "Razer Barracuda" }, { 0x056C, "Razer Barracuda X" }
        };

        static bool Generic(string product)
        {
            string p = product.ToLowerInvariant();
            return p == "hid interface" || p.Contains("receiver") || p.Contains("dongle") || p == "usb device" || p.StartsWith("hid ");
        }

        public void Poll(List<Reading> outList)
        {
            var byPid = new Dictionary<ushort, List<Native.HidDev>>();
            foreach (var d in Native.HidDevices(v => v == 0x1532))
            {
                List<Native.HidDev> l;
                if (!byPid.TryGetValue(d.Pid, out l)) byPid[d.Pid] = l = new List<Native.HidDev>();
                l.Add(d);
            }
            foreach (var kv in byPid)
            {
                string name = null, usageKind = null;
                foreach (var d in kv.Value)
                {
                    if (name == null && !string.IsNullOrEmpty(d.Product) && !Generic(d.Product)) name = d.Product;
                    if (d.UsagePage == 1 && d.Usage == 2) usageKind = "mouse";
                    if (d.UsagePage == 1 && d.Usage == 6 && usageKind == null) usageKind = "keyboard";
                }
                Reading r = null;
                foreach (var d in kv.Value)
                    if (r == null && d.FeatLen == 91) r = Standard(d);
                if (r == null)
                    foreach (var d in kv.Value)
                        if (r == null && d.UsagePage == 0xFF00 && d.FeatLen == 64 && d.InLen == 64 && d.OutLen == 64)
                        {
                            r = Headset(d);
                            if (r != null) usageKind = "headset";
                        }
                if (r == null) continue;
                string known;
                r.Name = name ?? (KnownNames.TryGetValue(kv.Key, out known) ? known : "Razer device " + kv.Key.ToString("X4"));
                r.Key = "razer:" + kv.Key.ToString("X4");
                r.Kind = Kinds.Guess(r.Name, usageKind ?? "other");
                r.Source = "Razer";
                Poller.Mark(0x1532, kv.Key, "Razer");
                outList.Add(r);
            }
        }

        // Razer mice/keyboards: 90-byte feature report, command class 0x07 (power).
        Reading Standard(Native.HidDev d)
        {
            byte cached;
            byte[] list = tids.TryGetValue(d.Path, out cached) ? new[] { cached } : new byte[] { 0x1F, 0x3F, 0xFF, 0x9F };
            using (var h = Native.Open(d.Path, Native.ReadWrite, false))
            {
                if (h.IsInvalid) return null;
                foreach (byte tid in list)
                {
                    byte[] r = Command(h, tid, 0x07, 0x80);
                    if (r == null) continue;
                    tids[d.Path] = tid;
                    if (r[10] == 0) return null; // wired-only devices answer 0
                    var res = new Reading();
                    res.Pct = (r[10] * 100 + 127) / 255;
                    byte[] c = Command(h, tid, 0x07, 0x84);
                    res.Charging = c != null && c[10] == 1;
                    return res;
                }
            }
            return null;
        }

        static byte[] Command(SafeFileHandle h, byte tid, byte cls, byte id)
        {
            var b = new byte[91];
            b[2] = tid; b[6] = 0x02; b[7] = cls; b[8] = id;
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= b[i];
            b[89] = crc;
            if (!Native.HidD_SetFeature(h, b, b.Length)) return null;
            for (int k = 0; k < 6; k++)
            {
                Thread.Sleep(k == 0 ? 40 : 100);
                var r = new byte[91];
                if (!Native.HidD_GetFeature(h, r, r.Length)) return null;
                if (r[1] == 0x01) continue; // busy
                return r[1] == 0x02 && r[7] == cls && r[8] == id ? r : null;
            }
            return null;
        }

        // Razer HyperSpeed headsets (BlackShark V2 Pro family): 64-byte feature report 0xFF.
        static Reading Headset(Native.HidDev d)
        {
            using (var h = Native.Open(d.Path, Native.ReadWrite, false))
            {
                if (h.IsInvalid) return null;
                var b = new byte[64];
                byte[] req = { 0xFF, 0x0A, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x02, 0x05 };
                Array.Copy(req, b, req.Length);
                if (!Native.HidD_SetFeature(h, b, b.Length)) return null;
                for (int k = 0; k < 8; k++)
                {
                    Thread.Sleep(100);
                    var r = new byte[64];
                    r[0] = 0xFF;
                    if (!Native.HidD_GetFeature(h, r, r.Length)) return null;
                    if (r[2] == 0x05 && r[3] == 0xFE && r[4] == 0x12 && r[5] == 0x04 && r[6] == 0x1F && r[7] == 0x08 && r[8] == 0x05)
                    {
                        var res = new Reading();
                        res.Pct = Math.Min(100, (int)r[14]);
                        int s = r[9];
                        res.Charging = s == 5 || s == 6 || (s >= 8 && s <= 10);
                        return res.Pct > 0 ? res : null;
                    }
                }
            }
            return null;
        }
    }

    // ---------------- Anything Windows reports a battery for (mostly Bluetooth) ----------------
    class WindowsSource
    {
        public void Poll(List<Reading> outList)
        {
            var map = new Dictionary<string, Reading>();
            foreach (var p in Native.WindowsBatteryDevices())
            {
                string key = "win:" + (p.Container ?? p.Name ?? "?");
                Reading r;
                if (map.TryGetValue(key, out r))
                {
                    if (p.Name != null && (r.Name == null || p.Name.Length < r.Name.Length)) r.Name = p.Name;
                    continue;
                }
                r = new Reading();
                r.Key = key; r.Name = p.Name; r.Pct = Math.Min(100, p.Battery); r.Source = "Windows";
                map[key] = r;
            }
            foreach (var r in map.Values)
            {
                if (r.Name == null) r.Name = "Bluetooth device";
                r.Kind = Kinds.Guess(r.Name, "other");
                outList.Add(r);
            }
        }
    }

    class Poller
    {
        readonly GHubSource ghub = new GHubSource();
        readonly HidppSource hidpp = new HidppSource();
        readonly RazerSource razer = new RazerSource();
        readonly WindowsSource windows = new WindowsSource();
        readonly SteelSeriesSource steel = new SteelSeriesSource();
        readonly HyperXSource hyperx = new HyperXSource();
        readonly GenericHidSource generic = new GenericHidSource();
        public string Log = "";

        // Which source handled a USB device ("VVVV:PPPP", or "VVVV:*" for a whole vendor). Reset every poll.
        static readonly Dictionary<string, string> handled = new Dictionary<string, string>();
        public static readonly StringBuilder Notes = new StringBuilder();
        public static void Mark(ushort vid, ushort pid, string who) { handled[vid.ToString("X4") + ":" + pid.ToString("X4")] = who; }
        public static void MarkVendor(ushort vid, string who) { handled[vid.ToString("X4") + ":*"] = who; }
        public static string HandledBy(ushort vid, ushort pid)
        {
            string w;
            if (handled.TryGetValue(vid.ToString("X4") + ":" + pid.ToString("X4"), out w)) return w;
            if (handled.TryGetValue(vid.ToString("X4") + ":*", out w)) return w;
            return null;
        }
        public static bool Demo;

        public List<Reading> PollAll(bool forceHidpp)
        {
            var all = new List<Reading>();
            if (Demo)
            {
                all.Add(new Reading { Key = "demo:1", Name = "Demo Headphones", Kind = "headset", Pct = 30, Source = "Demo" });
                all.Add(new Reading { Key = "demo:2", Name = "Demo Mouse", Kind = "mouse", Pct = 93, Source = "Demo" });
                all.Add(new Reading { Key = "demo:3", Name = "Demo Keyboard", Kind = "keyboard", Pct = 64, Charging = true, Source = "Demo" });
                Log = "demo mode";
                return all;
            }
            handled.Clear();
            Notes.Length = 0;
            Native.ResetHid();
            var log = new StringBuilder();
            log.AppendLine("Battery Bar " + Program.Version + " on " + Environment.OSVersion.VersionString + " at " + DateTime.Now);
            if (Run("Logitech G HUB", ghub.Poll, all, log).Count > 0) MarkVendor(0x046D, "Logitech G HUB");
            log.AppendLine("  G HUB: " + ghub.Note);
            if (!ghub.Available || forceHidpp) { if (Run("Logitech direct", hidpp.Poll, all, log).Count > 0) MarkVendor(0x046D, "Logitech direct"); }
            else log.AppendLine("Logitech direct: skipped (G HUB is running)");
            Run("Razer", razer.Poll, all, log);
            Run("SteelSeries", steel.Poll, all, log);
            Run("HyperX", hyperx.Poll, all, log);
            Run("Generic HID battery", generic.Poll, all, log);
            Run("Windows / Bluetooth", windows.Poll, all, log);
            try { DongleReport.Write(log); } catch (Exception e) { log.AppendLine("dongle report: error " + e.Message); }
            Log = log.ToString();
            return all;
        }

        static List<Reading> Run(string name, Action<List<Reading>> poll, List<Reading> all, StringBuilder log)
        {
            var list = new List<Reading>();
            var sw = Stopwatch.StartNew();
            try { poll(list); }
            catch (Exception e) { log.AppendLine(name + ": error " + e.GetType().Name + ": " + e.Message); }
            log.AppendLine(name + ": " + list.Count + " device(s), " + sw.ElapsedMilliseconds + " ms");
            foreach (var r in list)
                log.AppendLine("  " + r.Name + " [" + r.Kind + "] " + r.Pct + "%" + (r.Charging ? " charging" : "") + "  (" + r.Key + ")");
            if (Notes.Length > 0) { log.Append(Notes.ToString()); Notes.Length = 0; }
            all.AddRange(list);
            return list;
        }
    }
}
