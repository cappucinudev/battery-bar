using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace BatteryBar
{
    // Shared HID helpers for the dongle sources below.
    static class HidIo
    {
        // Write one output report and read the answer (read-only "get" requests only).
        // req[0] is the report id (0 = unnumbered). The result is shaped like hidapi's hid_read:
        // a leading report id of 0 is removed, a real id stays at index 0.
        public static byte[] Xfer(Native.HidDev d, byte[] req, int timeoutMs, Func<byte[], bool> accept)
        {
            if (d.OutLen <= 0 || d.InLen <= 0 || req.Length > d.OutLen) return null;
            using (var h = Native.Open(d.Path, Native.ReadWrite, true))
            {
                if (h.IsInvalid) return null;
                using (var ch = new HidChannel(h, d.InLen))
                {
                    for (int i = 0; i < 8; i++) if (ch.Read(1) == null) break; // drop stale reports
                    var w = new byte[d.OutLen];
                    Array.Copy(req, w, req.Length);
                    try { ch.Write(w); } catch { return null; }
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        byte[] r;
                        try { r = ch.Read((int)(timeoutMs - sw.ElapsedMilliseconds)); } catch { return null; }
                        if (r == null) break;
                        byte[] data = r;
                        if (r.Length > 0 && r[0] == 0) { data = new byte[r.Length - 1]; Array.Copy(r, 1, data, 0, data.Length); }
                        if (accept == null || accept(data)) return data;
                    }
                }
            }
            return null;
        }

        // Interfaces of one device to try: exact usage page/usage first, then the USB interface number.
        public static List<Native.HidDev> Candidates(List<Native.HidDev> devs, ushort page, ushort usage, int iface)
        {
            var list = new List<Native.HidDev>();
            if (page != 0)
                foreach (var d in devs) if (d.UsagePage == page && d.Usage == usage && d.OutLen > 0 && d.InLen > 0) list.Add(d);
            if (iface >= 0)
            {
                string mi = "mi_" + iface.ToString("x2");
                foreach (var d in devs)
                    if (!list.Contains(d) && d.OutLen > 0 && d.InLen > 0 && d.Path.ToLowerInvariant().Contains(mi)) list.Add(d);
            }
            if (list.Count > 3) list.RemoveRange(3, list.Count - 3);
            return list;
        }

        // Vendor-defined interfaces that can take an output report.
        public static List<Native.HidDev> VendorPages(List<Native.HidDev> devs)
        {
            var list = new List<Native.HidDev>();
            foreach (var d in devs) if (d.UsagePage >= 0xFF00 && d.OutLen > 0 && d.InLen > 0) list.Add(d);
            if (list.Count > 4) list.RemoveRange(4, list.Count - 4);
            return list;
        }

        public static Dictionary<ushort, List<Native.HidDev>> ByPid(List<Native.HidDev> devs)
        {
            var map = new Dictionary<ushort, List<Native.HidDev>>();
            foreach (var d in devs)
            {
                List<Native.HidDev> l;
                if (!map.TryGetValue(d.Pid, out l)) map[d.Pid] = l = new List<Native.HidDev>();
                l.Add(d);
            }
            return map;
        }

        public static string FirstProduct(List<Native.HidDev> devs)
        {
            foreach (var d in devs) if (!string.IsNullOrEmpty(d.Product)) return d.Product;
            return null;
        }

        public static int Clamp(int v) { return v < 0 ? 0 : (v > 100 ? 100 : v); }
    }

    // ---------------- SteelSeries wireless headsets ----------------
    // Requests copied from HeadsetControl (github.com/Sapd/HeadsetControl, lib/devices/steelseries_*.hpp).
    // Only the battery "get" request is sent. Arctis Pro Wireless (0x1290), Arctis 9 rev 0x12C4 and the
    // wireless mice are left out: the Pro Wireless exchange is unclear and the others are unverified.
    class SteelSeriesSource
    {
        class Model
        {
            public ushort Pid; public string Name, Proto; public ushort Page, Usage; public int Iface;
            public Model(ushort pid, string name, string proto, ushort page, ushort usage, int iface)
            { Pid = pid; Name = name; Proto = proto; Page = page; Usage = usage; Iface = iface; }
        }

        static readonly ushort[] DiscreteNova7 = { 0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4 };

        static readonly Model[] Models =
        {
            // Arctis 7 / Pro 2019: request 06 18, battery % in byte 2, vendor collection FF43:0202 (interface 5)
            new Model(0x1260, "Arctis 7", "a7", 0xFF43, 0x0202, 5),
            new Model(0x12AD, "Arctis 7 (2019)", "a7", 0xFF43, 0x0202, 5),
            new Model(0x1252, "Arctis Pro (2019)", "a7", 0xFF43, 0x0202, 5),
            // Arctis 9: request 00 20, battery byte 3 (0x64..0x9A), status byte 4 (interface 0)
            new Model(0x12C2, "Arctis 9", "a9", 0, 0, 0),
            // Arctis Nova 7 family: request 00 B0, battery byte 2, status byte 3, vendor collection FFC0:0001 (interface 3)
            new Model(0x2202, "Arctis Nova 7", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x22A1, "Arctis Nova 7", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x227E, "Arctis Nova 7 Wireless Gen 2", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x2206, "Arctis Nova 7X", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x2258, "Arctis Nova 7X v2", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x229E, "Arctis Nova 7X v2", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x22AD, "Arctis Nova 7X v2", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x223A, "Arctis Nova 7 Diablo IV", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x22A9, "Arctis Nova 7 Diablo IV", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x227A, "Arctis Nova 7 WoW", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x22A4, "Arctis Nova 7X", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x22A5, "Arctis Nova 7X", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x220A, "Arctis Nova 7", "n7", 0xFFC0, 0x0001, 3),
            new Model(0x22A7, "Arctis Nova 7", "n7", 0xFFC0, 0x0001, 3),
            // Arctis Nova Pro Wireless base station: request 06 B0, battery byte 6 (0..8), status byte 15 (interface 4)
            new Model(0x12E0, "Arctis Nova Pro Wireless", "np", 0, 0, 4),
            new Model(0x12E5, "Arctis Nova Pro Wireless X", "np", 0, 0, 4)
        };

        public void Poll(List<Reading> outList)
        {
            var devs = Native.HidDevices(v => v == 0x1038);
            if (devs.Count == 0) return;
            var byPid = HidIo.ByPid(devs);
            var done = new Dictionary<ushort, bool>();
            foreach (var m in Models)
            {
                List<Native.HidDev> l;
                if (!byPid.TryGetValue(m.Pid, out l) || done.ContainsKey(m.Pid)) continue;
                done[m.Pid] = true;
                Poller.Mark(0x1038, m.Pid, "SteelSeries (known model)");
                Reading r = null;
                foreach (var d in HidIo.Candidates(l, m.Page, m.Usage, m.Iface))
                {
                    r = Query(m, d);
                    if (r != null) break;
                }
                if (r == null) { Poller.Notes.AppendLine("  SteelSeries 1038:" + m.Pid.ToString("X4") + " " + m.Name + ": no answer (headset off?)"); continue; }
                string prod = HidIo.FirstProduct(l);
                r.Name = !string.IsNullOrEmpty(prod) && prod.ToLowerInvariant().Contains("arctis") ? prod : "SteelSeries " + m.Name;
                r.Key = "steelseries:" + m.Pid.ToString("X4");
                r.Kind = "headset";
                r.Source = "SteelSeries";
                outList.Add(r);
            }
        }

        static Reading Query(Model m, Native.HidDev d)
        {
            byte[] data;
            switch (m.Proto)
            {
                case "a7":
                    data = HidIo.Xfer(d, new byte[] { 0x06, 0x18 }, 800, x => x.Length >= 3);
                    if (data == null || data[2] == 0 || data[2] > 100) return null;
                    return Make(data[2], false);
                case "a9":
                    data = HidIo.Xfer(d, new byte[] { 0x00, 0x20 }, 800, x => x.Length >= 5);
                    if (data == null || data[4] == 0xFF) return null;
                    return Make(HidIo.Clamp((data[3] - 0x64) * 100 / (0x9A - 0x64)), data[4] == 0x01);
                case "n7":
                    data = HidIo.Xfer(d, new byte[] { 0x00, 0xB0 }, 800, x => x.Length >= 4);
                    if (data == null || data[3] == 0x00) return null; // 0 = headset not connected
                    int lvl = data[2];
                    if (Array.IndexOf(DiscreteNova7, m.Pid) >= 0) lvl = lvl * 25; // 0..4 steps
                    return Make(HidIo.Clamp(lvl), data[3] == 0x01 || data[3] == 0x02);
                case "np":
                    data = HidIo.Xfer(d, new byte[] { 0x06, 0xB0 }, 800, x => x.Length >= 16);
                    if (data == null || data[15] == 0x01) return null; // 1 = headset offline
                    return Make(HidIo.Clamp(data[6] * 100 / 8), data[15] == 0x02);
            }
            return null;
        }

        static Reading Make(int pct, bool charging)
        {
            var r = new Reading();
            r.Pct = pct; r.Charging = charging;
            return r;
        }
    }

    // ---------------- HyperX wireless headsets ----------------
    // Requests copied from HeadsetControl (lib/devices/hyperx_cloud_flight.hpp, hyperx_cloud_alpha_wireless.hpp,
    // hyperx_cloud_2_wireless.hpp). Cloud III Wireless, Alpha 2 Wireless and the Kingston Cloud II are not included.
    class HyperXSource
    {
        class Model
        {
            public ushort Vid, Pid; public string Name, Proto;
            public Model(ushort vid, ushort pid, string name, string proto) { Vid = vid; Pid = pid; Name = name; Proto = proto; }
        }

        static readonly Model[] Models =
        {
            new Model(0x0951, 0x16C4, "Cloud Flight", "flight"),
            new Model(0x0951, 0x1723, "Cloud Flight", "flight"),
            new Model(0x03F0, 0x098D, "Cloud Alpha Wireless", "alpha"),
            new Model(0x03F0, 0x0696, "Cloud II Wireless", "c2"),
            new Model(0x03F0, 0x018B, "Cloud II Wireless", "c2")
        };

        public void Poll(List<Reading> outList)
        {
            var devs = Native.HidDevices(v => v == 0x0951 || v == 0x03F0);
            if (devs.Count == 0) return;
            foreach (var m in Models)
            {
                var l = new List<Native.HidDev>();
                foreach (var d in devs) if (d.Vid == m.Vid && d.Pid == m.Pid) l.Add(d);
                if (l.Count == 0) continue;
                Poller.Mark(m.Vid, m.Pid, "HyperX (known model)");
                var cands = m.Proto == "c2" ? HidIo.Candidates(l, 0xFF90, 0x0303, 0) : HidIo.VendorPages(l);
                Reading r = null;
                foreach (var d in cands)
                {
                    r = Query(m, d);
                    if (r != null) break;
                }
                if (r == null) { Poller.Notes.AppendLine("  HyperX " + m.Vid.ToString("X4") + ":" + m.Pid.ToString("X4") + " " + m.Name + ": no answer (headset off, or charging)"); continue; }
                string prod = HidIo.FirstProduct(l);
                r.Name = !string.IsNullOrEmpty(prod) && prod.ToLowerInvariant().Contains("cloud") ? prod : "HyperX " + m.Name;
                r.Key = "hyperx:" + m.Pid.ToString("X4");
                r.Kind = "headset";
                r.Source = "HyperX";
                outList.Add(r);
            }
        }

        static Reading Query(Model m, Native.HidDev d)
        {
            var res = new Reading();
            if (m.Proto == "flight")
            {
                var req = new byte[20];
                req[0] = 0x21; req[1] = 0xFF; req[2] = 0x05;
                var data = HidIo.Xfer(d, req, 1000, x => x.Length >= 5);
                if (data == null) return null;
                int v = (data[3] << 8) | data[4];
                if (v > 0x100B) { Poller.Notes.AppendLine("  HyperX Cloud Flight: charging, level unknown"); return null; }
                res.Pct = HidIo.Clamp((int)Math.Round(FlightLevel(v)));
                return res;
            }
            if (m.Proto == "alpha")
            {
                var conn = HidIo.Xfer(d, new byte[] { 0x21, 0xBB, 0x03 }, 1000, x => x.Length >= 4);
                if (conn == null || conn[3] == 0x01) return null; // 1 = headset not connected
                var chg = HidIo.Xfer(d, new byte[] { 0x21, 0xBB, 0x0C }, 1000, x => x.Length >= 4);
                if (chg == null) return null;
                if (chg[3] == 0x01) { Poller.Notes.AppendLine("  HyperX Cloud Alpha Wireless: charging, level unknown"); return null; }
                var bat = HidIo.Xfer(d, new byte[] { 0x21, 0xBB, 0x0B }, 1000, x => x.Length >= 4);
                if (bat == null || bat[3] > 100) return null;
                res.Pct = bat[3];
                return res;
            }
            // Cloud II Wireless: 06 FF BB <cmd>, answer echoes the header
            var lvl = C2(d, 0x02);
            if (lvl == null || lvl[7] > 100) return null;
            res.Pct = lvl[7];
            var ch = C2(d, 0x03);
            res.Charging = ch != null && ch[4] == 1;
            return res;
        }

        static byte[] C2(Native.HidDev d, byte cmd)
        {
            return HidIo.Xfer(d, new byte[] { 0x06, 0xFF, 0xBB, cmd, 0x00 }, 1000,
                x => x.Length >= 8 && x[0] == 0x06 && x[1] == 0xFF && x[2] == 0xBB && x[3] == cmd);
        }

        // Voltage (mV) to percent, curve from HeadsetControl hyperx_cloud_flight.hpp.
        static double FlightLevel(int voltage)
        {
            if (voltage <= 3648) return 0.00125 * voltage;
            if (voltage > 3975) return 100.0;
            double v = voltage;
            return 0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3) + 2.238321 * Math.Pow(v, 2) - 5706.256 * v + 5452299.0;
        }
    }

    // ---------------- MCHOSE mice (A7 V2 family, L7 Pro) ----------------
    // Protocol of MCHOSE's own M HUB WebHID driver, as reverse-engineered and verified on an A7 V2 Ultra+
    // on its 2.4G receiver by the OpenMouse project: npm @openmouse/protocol (dist/mchose/index.js,
    // dist/drivers/mchose/hid.js), https://github.com/OpenMouse-Project/openmouse ; also
    // https://github.com/alexfrih/mchose-linux (L7 Pro). Vendor collection 0xFF01/0x0001, feature report 0x11:
    // command 0x06 = "get mouse identity + battery". Every byte of the command and the reply is bit-inverted.
    // Reply payload (after un-inverting): [0..1] vid, [2..3] mouse pid, [8] flags, [9] battery %, [10] charging.
    // Receivers: 3837:100B (2.4G), 3837:100A (Bluetooth dongle id), 5253:1020 (8K receiver). Wired mice 3837:4018/4019/4021/4023,
    // 5253:1011. The MagDock (1012) and A7 V3 ids use other protocols and are left out. UNTESTED on real hardware here.
    class MchoseSource
    {
        static readonly ushort[] Skip = { 0x1012, 0x1014, 0x1018, 0x4030, 0x4031, 0x4032, 0x4033 };

        static string ModelName(int pid)
        {
            switch (pid)
            {
                case 0x4018: return "A7 V2 Pro";
                case 0x4023: return "A7 V2 Pro+";
                case 0x4019: return "A7 V2 Ultra";
                case 0x4021: return "A7 V2 Ultra+";
                case 0x1011: return "A7 Ultra";
                case 0x00B0: return "L7 Pro";
                case 0x4015: return "L7 Pro+";
            }
            return null;
        }

        static bool Wanted(ushort vid, ushort pid)
        {
            if (vid == 0x3837) return Array.IndexOf(Skip, pid) < 0;
            return vid == 0x5253 && (pid == 0x1020 || pid == 0x1011);
        }

        public void Poll(List<Reading> outList)
        {
            var devs = Native.HidDevices(v => v == 0x3837 || v == 0x5253);
            if (devs.Count == 0) return;
            foreach (var kv in HidIo.ByPid(devs))
            {
                var l = kv.Value;
                if (!Wanted(l[0].Vid, l[0].Pid)) continue;
                Native.HidDev dev = null;
                foreach (var d in l) if (d.UsagePage == 0xFF01 && d.Usage == 0x0001 && d.FeatLen >= 21) { dev = d; break; }
                if (dev == null) continue;
                Poller.Mark(dev.Vid, dev.Pid, "MCHOSE");
                byte[] p = Query(dev);
                string id = dev.Vid.ToString("X4") + ":" + dev.Pid.ToString("X4");
                if (p == null) { Poller.Notes.AppendLine("  MCHOSE " + id + ": no answer (mouse asleep or off)"); continue; }
                int mousePid = p[2] | (p[3] << 8);
                string model = ModelName(mousePid) ?? ModelName(dev.Pid);
                string prod = HidIo.FirstProduct(l);
                var r = new Reading();
                r.Pct = p[9];
                r.Charging = p[10] != 0;
                r.Name = !string.IsNullOrEmpty(prod) && prod.ToLowerInvariant().Contains("mchose") ? prod : (model != null ? "MCHOSE " + model : "MCHOSE mouse");
                r.Key = "mchose:" + dev.Pid.ToString("X4");
                r.Kind = "mouse";
                r.Source = "MCHOSE";
                outList.Add(r);
            }
        }

        // Send command 0x06 (inverted) on feature report 0x11 and read it back; needs two equal answers.
        static byte[] Query(Native.HidDev d)
        {
            using (var h = Native.Open(d.Path, Native.ReadWrite, false))
            {
                if (h.IsInvalid) return null;
                int len = Math.Max((int)d.FeatLen, 65);
                var req = new byte[len];
                req[0] = 0x11;
                req[1] = 0xF9; // ~0x06
                for (int i = 2; i <= 20; i++) req[i] = 0xFF; // 19 argument bytes of 0, inverted
                byte[] prev = null;
                var sw = Stopwatch.StartNew();
                for (int k = 0; k < 6 && sw.ElapsedMilliseconds < 800; k++)
                {
                    if (!Native.HidD_SetFeature(h, req, len)) return null;
                    System.Threading.Thread.Sleep(90);
                    var r = new byte[len];
                    r[0] = 0x11;
                    if (!Native.HidD_GetFeature(h, r, len)) return null;
                    if ((byte)~r[1] != 0x06) continue;
                    var p = new byte[11];
                    bool any = false;
                    for (int i = 0; i < 11; i++) { p[i] = (byte)~r[i + 2]; if (p[i] != 0) any = true; }
                    if (!any || p[9] > 100) continue;
                    if (prev != null && Same(prev, p)) return p;
                    prev = p;
                }
            }
            return null;
        }

        static bool Same(byte[] a, byte[] b) { for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    }

    // ---------------- ATK / VXE / Compx mice ----------------
    // Command 0x04 "get battery" of the ATK HUB protocol: 16-byte frame on output report 0x08
    // [cmd, status, addr(2), len, data(10), checksum], checksum = 0x55 - (0x08 + sum of the bytes before it).
    // Sources: https://github.com/dkbednarczyk/madr (madr-lib/src/battery.rs, VXE MAD R, 373B:103F wired / 373B:1040 dongle),
    // https://github.com/cyberphantom52/libatk-rs (GetBatteryLevel = command 4, VXE R1 Pro), and npm @openmouse/protocol
    // dist/drivers/atk/hid.js (collection 0xFF02/0x0002 on VID 373B, and on VID 3554 PIDs F58A/F58C/F58E/F58F).
    // Reply (report id kept at [0]): [0]=08 [1]=04 [6]=battery % [7]=charging [8..9]=millivolts. UNTESTED here.
    class AtkSource
    {
        static readonly ushort[] CompxPids = { 0xF58A, 0xF58C, 0xF58E, 0xF58F };

        public void Poll(List<Reading> outList)
        {
            var devs = Native.HidDevices(v => v == 0x373B || v == 0x3554);
            if (devs.Count == 0) return;
            foreach (var kv in HidIo.ByPid(devs))
            {
                var l = kv.Value;
                ushort vid = l[0].Vid, pid = l[0].Pid;
                if (vid == 0x3554 && Array.IndexOf(CompxPids, pid) < 0) continue;
                var cands = HidIo.Candidates(l, 0xFF02, 0x0002, -1);
                if (cands.Count == 0) continue;
                Poller.Mark(vid, pid, "ATK/VXE");
                var req = new byte[17];
                req[0] = 0x08; req[1] = 0x04; req[16] = 0x49; // 0x55 - (0x08 + 0x04)
                byte[] x = HidIo.Xfer(cands[0], req, 500, a => a.Length >= 10 && a[0] == 0x08 && a[1] == 0x04);
                string id = vid.ToString("X4") + ":" + pid.ToString("X4");
                if (x == null || x[6] > 100 || (x[6] == 0 && x[8] == 0 && x[9] == 0)) { Poller.Notes.AppendLine("  ATK/VXE " + id + ": no battery answer (mouse asleep or off)"); continue; }
                string prod = HidIo.FirstProduct(l);
                var r = new Reading();
                r.Pct = x[6];
                r.Charging = x[7] == 1;
                r.Name = string.IsNullOrEmpty(prod) ? "ATK/VXE mouse" : prod;
                r.Key = "atk:" + pid.ToString("X4");
                r.Kind = "mouse";
                r.Source = "ATK/VXE";
                outList.Add(r);
            }
        }
    }

    // ---------------- Any USB HID device that reports a battery usage ----------------
    // Generic Device Controls 0x06/0x20 (Battery Strength), Battery System 0x85/0x65 (AbsoluteStateOfCharge)
    // and 0x85/0x66 (RemainingCapacity), with 0x85/0x44 (Charging) and 0x85/0x45 (Discharging) when present.
    // Read-only GET_REPORT. Skips devices another source already handled, UPS (page 0x84) and Bluetooth
    // (the Windows source covers that).
    class GenericHidSource
    {
        static readonly ushort[] UpsVids = { 0x0463, 0x051D, 0x09AE, 0x0764 }; // MGE, APC, Tripp Lite, CyberPower
        readonly Dictionary<string, int> fails = new Dictionary<string, int>();
        readonly Dictionary<string, DateTime> pause = new Dictionary<string, DateTime>();

        public static bool IsBatteryUsage(Native.HidCap c)
        {
            return (c.Page == 0x06 && c.Usage == 0x20) || (c.Page == 0x85 && (c.Usage == 0x65 || c.Usage == 0x66));
        }

        public void Poll(List<Reading> outList)
        {
            var groups = new Dictionary<string, List<Native.HidDev>>();
            foreach (var d in Native.HidDevices(v => true))
            {
                if (!d.Path.ToLowerInvariant().Contains("vid_")) continue; // USB only
                List<Native.HidDev> l;
                if (!groups.TryGetValue(d.Id, out l)) groups[d.Id] = l = new List<Native.HidDev>();
                l.Add(d);
            }
            foreach (var kv in groups)
            {
                var devs = kv.Value;
                ushort vid = devs[0].Vid, pid = devs[0].Pid;
                if (Poller.HandledBy(vid, pid) != null || Array.IndexOf(UpsVids, vid) >= 0) continue;
                Native.HidDev bd = null; Native.HidCap bc = null;
                bool power = false;
                foreach (var d in devs)
                {
                    if (d.UsagePage == 0x84) power = true;
                    foreach (var c in d.Caps)
                    {
                        if (c.Page == 0x84) power = true;
                        if (bc == null && IsBatteryUsage(c)) { bd = d; bc = c; }
                    }
                }
                if (power || bc == null) continue;
                DateTime until;
                if (pause.TryGetValue(kv.Key, out until) && DateTime.UtcNow < until) continue;
                long v;
                if (!Native.ReadUsage(bd, bc, out v))
                {
                    int n;
                    fails.TryGetValue(kv.Key, out n);
                    fails[kv.Key] = ++n;
                    if (n >= 3) pause[kv.Key] = DateTime.UtcNow.AddMinutes(10);
                    Poller.Notes.AppendLine("  " + kv.Key + " has battery usage " + bc.Page.ToString("X2") + "/" + bc.Usage.ToString("X2") + " but the read failed");
                    continue;
                }
                fails.Remove(kv.Key);
                long min = bc.Min, max = bc.Max;
                if (max <= min) { min = 0; max = 100; }
                var r = new Reading();
                r.Pct = HidIo.Clamp((int)((v - min) * 100 / (max - min)));
                foreach (var d in devs)
                    foreach (var c in d.Caps)
                        if (c.Page == 0x85 && (c.Usage == 0x44 || c.Usage == 0x45))
                        {
                            long cv;
                            if (Native.ReadUsage(d, c, out cv) && cv != 0) r.Charging = c.Usage == 0x44;
                        }
                string name = HidIo.FirstProduct(devs) ?? ("USB device " + kv.Key);
                r.Name = name;
                r.Key = "hid:" + kv.Key;
                r.Kind = Kinds.Guess(name, UsageKind(devs));
                r.Source = "HID";
                Poller.Mark(vid, pid, "Generic HID battery");
                outList.Add(r);
            }
        }

        // Kind from the collections: mouse 01/02, keyboard 01/06, headset when it also has a telephony headset usage.
        static string UsageKind(List<Native.HidDev> devs)
        {
            string k = null;
            foreach (var d in devs)
            {
                if (d.UsagePage == 1 && d.Usage == 2) return "mouse";
                if (d.UsagePage == 1 && d.Usage == 6) k = "keyboard";
                if (d.UsagePage == 0x0B && d.Usage == 0x05 && k == null) k = "headset";
            }
            return k ?? "other";
        }
    }

    // ---------------- Log section for unknown dongles ----------------
    static class DongleReport
    {
        static readonly string[] Words = { "receiver", "dongle", "wireless", "2.4", "lightspeed", "hyperspeed", "slipstream" };

        public static void Write(StringBuilder log)
        {
            log.AppendLine("USB receivers / dongles (paste this when a device is missing):");
            int count = 0;
            try
            {
                var groups = new Dictionary<string, List<Native.HidDev>>();
                foreach (var d in Native.HidDevices(v => true))
                {
                    if (!d.Path.ToLowerInvariant().Contains("vid_")) continue;
                    List<Native.HidDev> l;
                    if (!groups.TryGetValue(d.Id, out l)) groups[d.Id] = l = new List<Native.HidDev>();
                    l.Add(d);
                }
                foreach (var kv in groups)
                {
                    var devs = kv.Value;
                    string prod = HidIo.FirstProduct(devs);
                    bool look = false, vendor = false, input = false;
                    string pl = (prod ?? "").ToLowerInvariant();
                    foreach (string w in Words) if (pl.Contains(w)) look = true;
                    foreach (var d in devs)
                    {
                        if (d.UsagePage >= 0xFF00) vendor = true;
                        if ((d.UsagePage == 1 && (d.Usage == 1 || d.Usage == 2 || d.Usage == 6)) || d.UsagePage == 0x0C) input = true;
                    }
                    if (vendor && input) look = true;
                    if (!look) continue;
                    count++;
                    string by = Poller.HandledBy(devs[0].Vid, devs[0].Pid);
                    log.AppendLine("  " + kv.Key + " " + (prod ?? "(no name)") + "  [" + (by != null ? "handled: " + by : "not handled") + "]");
                    foreach (var d in devs)
                    {
                        var line = new StringBuilder("    " + d.UsagePage.ToString("X4") + "/" + d.Usage.ToString("X4") + " in=" + d.InLen + " out=" + d.OutLen + " feat=" + d.FeatLen);
                        foreach (var c in d.Caps)
                            if (GenericHidSource.IsBatteryUsage(c) || (c.Page == 0x85 && (c.Usage == 0x44 || c.Usage == 0x45)))
                                line.Append("  battery-usage " + c.Page.ToString("X2") + "/" + c.Usage.ToString("X2") + " " + (c.Type == 'f' ? "feature" : "input") + " id=" + c.ReportId);
                        log.AppendLine(line.ToString());
                    }
                }
            }
            catch (Exception e) { log.AppendLine("  error " + e.GetType().Name + ": " + e.Message); }
            log.AppendLine("  " + count + " receiver-like device(s)");
        }
    }
}
