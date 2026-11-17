using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    // Win32, HID and SetupAPI calls used by the app.
    static class Native
    {
        // ---- windows / drawing ----
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);

        public struct RECT { public int Left, Top, Right, Bottom; }
        public struct SIZE { public int cx, cy; }
        public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        // ---- files ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        public const uint ReadWrite = 0xC0000000;

        public static SafeFileHandle Open(string path, uint access, bool overlapped)
        {
            return CreateFile(path, access, 3, IntPtr.Zero, 3, overlapped ? 0x40000000u : 0u, IntPtr.Zero);
        }

        // ---- HID ----
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pd);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pd);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pd, out HIDP_CAPS caps);
        [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);

        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputLen, OutputLen, FeatureLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest;
        }

        public class HidDev
        {
            public string Path, Product;
            public ushort Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen;
        }

        // Every present HID interface whose vendor id passes the filter.
        public static List<HidDev> HidDevices(Func<ushort, bool> vidOk)
        {
            var list = new List<HidDev>();
            Guid g;
            HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == new IntPtr(-1)) return list;
            try
            {
                var d = new SP_DEVICE_INTERFACE_DATA();
                d.cbSize = Marshal.SizeOf(d);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
                {
                    string path = InterfacePath(set, ref d);
                    if (path == null) continue;
                    using (var h = Open(path, 0, false))
                    {
                        if (h.IsInvalid) continue;
                        var a = new HIDD_ATTRIBUTES();
                        a.Size = Marshal.SizeOf(a);
                        if (!HidD_GetAttributes(h, ref a) || !vidOk(a.VendorID)) continue;
                        var dev = new HidDev();
                        dev.Path = path; dev.Vid = a.VendorID; dev.Pid = a.ProductID;
                        IntPtr pd;
                        if (HidD_GetPreparsedData(h, out pd))
                        {
                            HIDP_CAPS c;
                            HidP_GetCaps(pd, out c);
                            HidD_FreePreparsedData(pd);
                            dev.UsagePage = c.UsagePage; dev.Usage = c.Usage;
                            dev.InLen = c.InputLen; dev.OutLen = c.OutputLen; dev.FeatLen = c.FeatureLen;
                        }
                        var buf = new byte[256];
                        if (HidD_GetProductString(h, buf, buf.Length))
                        {
                            string s = Encoding.Unicode.GetString(buf);
                            int z = s.IndexOf('\0');
                            dev.Product = (z >= 0 ? s.Substring(0, z) : s).Trim();
                        }
                        list.Add(dev);
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        // ---- SetupAPI ----
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")] static extern IntPtr SetupDiGetAllDevs(IntPtr g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int idx, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out uint type, byte[] buf, int size, out int req, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }
        struct SP_DEVINFO_DATA { public int cbSize; public Guid classGuid; public int devInst; public IntPtr reserved; }
        struct DEVPROPKEY
        {
            public Guid fmtid; public int pid;
            public DEVPROPKEY(string g, int p) { fmtid = new Guid(g); pid = p; }
        }

        static string InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d)
        {
            int req;
            SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
            if (req <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(req);
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static DEVPROPKEY KeyBattery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static DEVPROPKEY KeyConnected = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);
        static DEVPROPKEY KeyFriendly = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static DEVPROPKEY KeyName = new DEVPROPKEY("B725F130-47EF-101A-A5F1-02608C9EEBAC", 10);
        static DEVPROPKEY KeyContainer = new DEVPROPKEY("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);

        public class PropDev { public string Name, Container; public int Battery; }

        // Devices Windows itself knows a battery level for (Bluetooth headsets, mice, keyboards, pads).
        // Only devices whose container is connected (or that do not say) are returned.
        public static List<PropDev> WindowsBatteryDevices()
        {
            var res = new List<PropDev>();
            var connected = new Dictionary<string, bool>();
            IntPtr set = SetupDiGetAllDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x6); // PRESENT | ALLCLASSES
            if (set == new IntPtr(-1)) return res;
            try
            {
                var d = new SP_DEVINFO_DATA();
                d.cbSize = Marshal.SizeOf(d);
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    string container = PropGuid(set, ref d, KeyContainer);
                    byte[] c = Prop(set, ref d, KeyConnected);
                    if (container != null && c != null && c.Length > 0)
                    {
                        bool was;
                        connected.TryGetValue(container, out was);
                        connected[container] = was || c[0] != 0;
                    }
                    byte[] b = Prop(set, ref d, KeyBattery);
                    if (b == null || b.Length < 1) continue;
                    var p = new PropDev();
                    p.Battery = b[0];
                    p.Container = container;
                    p.Name = PropString(set, ref d, KeyFriendly) ?? PropString(set, ref d, KeyName);
                    res.Add(p);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            res.RemoveAll(p => { bool on; return p.Container != null && connected.TryGetValue(p.Container, out on) && !on; });
            return res;
        }

        static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
        {
            uint type; int req;
            var buf = new byte[512];
            if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out type, buf, buf.Length, out req, 0)) return null;
            var r = new byte[Math.Min(req, buf.Length)];
            Array.Copy(buf, r, r.Length);
            return r;
        }

        static string PropString(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
        {
            byte[] b = Prop(set, ref d, key);
            if (b == null) return null;
            string s = Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
            return s.Length == 0 ? null : s;
        }

        static string PropGuid(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
        {
            byte[] b = Prop(set, ref d, key);
            if (b == null || b.Length != 16) return null;
            return new Guid(b).ToString();
        }
    }

    // Overlapped HID handle: write output reports, read input reports with a timeout.
    sealed class HidChannel : IDisposable
    {
        readonly FileStream fs;
        readonly int len;
        byte[] buf;
        IAsyncResult pending;

        public HidChannel(SafeFileHandle h, int inputLen)
        {
            fs = new FileStream(h, FileAccess.ReadWrite, 1, true);
            len = inputLen;
        }

        public void Write(byte[] b) { fs.Write(b, 0, b.Length); fs.Flush(); }

        public byte[] Read(int timeoutMs)
        {
            if (pending == null)
            {
                buf = new byte[len];
                pending = fs.BeginRead(buf, 0, len, null, null);
            }
            if (!pending.AsyncWaitHandle.WaitOne(Math.Max(1, timeoutMs))) return null;
            int n = fs.EndRead(pending);
            pending = null;
            var r = new byte[n];
            Array.Copy(buf, r, n);
            return r;
        }

        public void Dispose() { try { fs.Dispose(); } catch { } }
    }
}
