using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WaveIsland
{
    /// <summary>
    /// Gestes a 3 doigts geres par Wave Island : ceux de Windows sont coupes (registre du pave tactile),
    /// et on lit nous-memes les doigts du pave tactile (Raw Input, comme les applis de gestes) :
    /// 3 doigts vers le haut = vue des applis Wave, 3 doigts vers le bas = les applis rentrent dans le dock.
    /// </summary>
    public static class TouchpadGestures
    {
        // ================== Gestes de Windows (registre) ==================

        private const string PtpKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
        private const string BackupKey = @"Software\WaveIsland\TouchpadBackup";
        private static readonly string[] Values = { "ThreeFingerSlideEnabled", "ThreeFingerUp", "ThreeFingerDown", "ThreeFingerLeft", "ThreeFingerRight" };

        /// <summary>Coupe (ou remet) les gestes a 3 doigts de Windows selon le reglage.</summary>
        public static void Apply()
        {
            try
            {
                using var ptp = Registry.CurrentUser.CreateSubKey(PtpKey);
                using var bak = Registry.CurrentUser.CreateSubKey(BackupKey);
                if (Settings.Current.OwnThreeFinger)
                {
                    bool changed = false;
                    foreach (var v in Values)
                    {
                        var cur = ptp.GetValue(v);
                        if (cur == null) continue;
                        if (bak.GetValue(v) == null) bak.SetValue(v, cur, RegistryValueKind.DWord); // sauvegarde une seule fois
                        if (Convert.ToInt32(cur) != 0) { ptp.SetValue(v, 0, RegistryValueKind.DWord); changed = true; }
                    }
                    if (ptp.GetValue("ThreeFingerSlideEnabled") == null) { ptp.SetValue("ThreeFingerSlideEnabled", 0, RegistryValueKind.DWord); changed = true; }
                    if (changed) { Broadcast(); Log.I("3 doigts : gestes de Windows coupes, Wave Island les gere"); }
                }
                else
                {
                    bool changed = false;
                    foreach (var v in Values)
                    {
                        var old = bak.GetValue(v);
                        if (old == null) continue;
                        ptp.SetValue(v, old, RegistryValueKind.DWord);
                        bak.DeleteValue(v, false);
                        changed = true;
                    }
                    if (changed) { Broadcast(); Log.I("3 doigts : gestes de Windows remis"); }
                }
            }
            catch (Exception e) { Log.I("3 doigts : registre : " + e.Message); }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

        private static void Broadcast()
        {
            try { SendMessageTimeout(new IntPtr(0xffff), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "PrecisionTouchPad", 2 /* ABORTIFHUNG */, 500, out _); } catch { }
        }

        // ================== Lecture des doigts (Raw Input) ==================

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
        [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
        [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr raw, uint cmd, byte[] data, ref uint size, uint header);
        [DllImport("user32.dll")] private static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, IntPtr data, ref uint size);

        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, ushort[] caps);
        [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
        [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, uint len);
        [DllImport("hid.dll")] private static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] list, ref uint len, IntPtr pp, byte[] report, uint len2);
        private const int HIDP_OK = 0x00110000;

        private sealed class Device
        {
            public IntPtr Pp;
            public List<ushort> Fingers = new List<ushort>();   // collections « doigt » (qui ont X et Y)
            public double MaxY = 1, MaxX = 1;
            public Dictionary<uint, (double x, double y, int at)> Down = new Dictionary<uint, (double, double, int)>();
            public double StartY, StartX;
            public bool Tracking, Fired;
        }

        private static readonly Dictionary<IntPtr, Device> _devices = new Dictionary<IntPtr, Device>();
        private static HwndSource _sink;

        public static void Start()
        {
            Apply();
            try
            {
                _sink = new HwndSource(new HwndSourceParameters("WaveTouchpad") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 });
                _sink.AddHook(WndProc);
                var dev = new[] { new RAWINPUTDEVICE { UsagePage = 0x0D, Usage = 0x05, Flags = 0x00000100 /* RIDEV_INPUTSINK */, Target = _sink.Handle } };
                bool ok = RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
                Log.I("3 doigts : lecture du pave tactile " + (ok ? "active" : "impossible (" + Marshal.GetLastWin32Error() + ")"));
            }
            catch (Exception e) { Log.I("3 doigts : " + e.Message); }
        }

        private static IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x00FF /* WM_INPUT */ && Settings.Current.OwnThreeFinger)
            {
                try { Read(l); } catch (Exception e) { if (_errors++ < 3) Log.I("3 doigts : lecture : " + e.Message); }
            }
            return IntPtr.Zero;
        }

        private static int _errors;
        private static readonly int HeaderSize = 8 + 2 * IntPtr.Size;

        private static void Read(IntPtr raw)
        {
            uint size = 0;
            GetRawInputData(raw, 0x10000003 /* RID_INPUT */, null, ref size, (uint)HeaderSize);
            if (size == 0) return;
            var buf = new byte[size];
            if (GetRawInputData(raw, 0x10000003, buf, ref size, (uint)HeaderSize) != size) return;
            if (BitConverter.ToUInt32(buf, 0) != 2 /* RIM_TYPEHID */) return;
            IntPtr hDev = IntPtr.Size == 8 ? new IntPtr(BitConverter.ToInt64(buf, 8)) : new IntPtr(BitConverter.ToInt32(buf, 8));
            var dev = DeviceFor(hDev);
            if (dev == null) return;
            uint sizeHid = BitConverter.ToUInt32(buf, HeaderSize);
            uint count = BitConverter.ToUInt32(buf, HeaderSize + 4);
            for (uint i = 0; i < count; i++)
            {
                var report = new byte[sizeHid];
                Buffer.BlockCopy(buf, HeaderSize + 8 + (int)(i * sizeHid), report, 0, (int)sizeHid);
                Frame(dev, report);
            }
        }

        private static Device DeviceFor(IntPtr hDev)
        {
            if (_devices.TryGetValue(hDev, out var d)) return d;
            uint size = 0;
            GetRawInputDeviceInfo(hDev, 0x20000005 /* RIDI_PREPARSEDDATA */, IntPtr.Zero, ref size);
            if (size == 0) { _devices[hDev] = null; return null; }
            var pp = Marshal.AllocHGlobal((int)size);
            GetRawInputDeviceInfo(hDev, 0x20000005, pp, ref size);
            var caps = new ushort[32];
            if (HidP_GetCaps(pp, caps) != HIDP_OK) { _devices[hDev] = null; return null; }
            ushort nVal = caps[24];
            var vc = new byte[72 * Math.Max(1, (int)nVal)];
            if (HidP_GetValueCaps(0, vc, ref nVal, pp) != HIDP_OK) { _devices[hDev] = null; return null; }
            d = new Device { Pp = pp };
            var hasX = new HashSet<ushort>(); var hasY = new HashSet<ushort>();
            for (int i = 0; i < nVal; i++)
            {
                int o = i * 72;
                ushort page = BitConverter.ToUInt16(vc, o), link = BitConverter.ToUInt16(vc, o + 6);
                ushort usage = BitConverter.ToUInt16(vc, o + 56);
                int max = BitConverter.ToInt32(vc, o + 44);
                if (page == 0x01 && usage == 0x30) { hasX.Add(link); d.MaxX = Math.Max(1, max); }
                if (page == 0x01 && usage == 0x31) { hasY.Add(link); d.MaxY = Math.Max(1, max); }
            }
            d.Fingers = hasX.Intersect(hasY).OrderBy(x => x).ToList();
            Log.I($"3 doigts : pave tactile trouve ({d.Fingers.Count} doigts suivis)");
            _devices[hDev] = d;
            return d;
        }

        private static readonly ushort[] _usages = new ushort[16];

        private static void Frame(Device d, byte[] report)
        {
            uint len = (uint)report.Length;
            foreach (var link in d.Fingers)
            {
                if (HidP_GetUsageValue(0, 0x0D, link, 0x51 /* Contact ID */, out uint id, d.Pp, report, len) != HIDP_OK) continue;
                uint n = (uint)_usages.Length;
                bool tip = false;
                if (HidP_GetUsages(0, 0x0D, link, _usages, ref n, d.Pp, report, len) == HIDP_OK)
                    for (int i = 0; i < n; i++) if (_usages[i] == 0x42 /* Tip Switch */) tip = true;
                if (!tip) { d.Down.Remove(id); continue; }
                if (HidP_GetUsageValue(0, 0x01, link, 0x30, out uint x, d.Pp, report, len) != HIDP_OK) continue;
                if (HidP_GetUsageValue(0, 0x01, link, 0x31, out uint y, d.Pp, report, len) != HIDP_OK) continue;
                d.Down[id] = (x / d.MaxX, y / d.MaxY, Environment.TickCount);
            }
            Detect(d);
        }

        private static void Detect(Device d)
        {
            // Doigts qui n'ont plus donne de nouvelles (leve sans message) : oublies
            int now = Environment.TickCount;
            foreach (var k in d.Down.Where(p => now - p.Value.at > 150).Select(p => p.Key).ToList()) d.Down.Remove(k);
            int n = d.Down.Count;
            if (n != 3)
            {
                if (n < 3) { d.Tracking = false; d.Fired = false; }
                else d.Tracking = false; // 4 doigts ou plus : pas notre geste
                return;
            }
            double ay = d.Down.Values.Average(p => p.y), ax = d.Down.Values.Average(p => p.x);
            if (!d.Tracking) { d.Tracking = true; d.Fired = false; d.StartY = ay; d.StartX = ax; return; }
            if (d.Fired) return;
            double dy = ay - d.StartY, dx = ax - d.StartX;
            if (Math.Abs(dy) < 0.16 || Math.Abs(dy) < Math.Abs(dx) * 1.3) return;
            d.Fired = true;
            bool up = dy < 0;
            Log.I("3 doigts vers le " + (up ? "haut : vue des applis" : "bas : tout dans le dock"));
            if (up) AppMotion.BeginSwitcher();
            else AppMotion.SendAllToDock();
        }
    }
}
