using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using XrealDesktop.Host;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// The bundled Virtual Display Driver (MIT, github.com/VirtualDrivers/Virtual-Display-Driver) provides a fixed
    /// pool of virtual monitors (EDID "MTT1337"). Showing/hiding one screen = attaching/detaching that monitor to the
    /// Windows desktop, which is instant and needs no admin or driver reload.
    /// </summary>
    public static class VirtualScreens
    {
        public const string MonitorTag = "MTT1337";
        public const string AdapterTag = "MTTVDD";   // adapter device id "Root\MttVDD"
        public const int DefaultWidth = 1920, DefaultHeight = 1080;

        public sealed class Output
        {
            public string DeviceName;   // \\.\DISPLAYn
            public string MonitorId;
            public bool Attached;
            public RECT Bounds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, int idx, ref DISPLAY_DEVICE dd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsEx")] static extern int ApplyDisplaySettings(string device, IntPtr dm, IntPtr hwnd, int flags, IntPtr lParam);

        const int DM_POSITION = 0x20, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
        const int CDS_UPDATEREGISTRY = 0x1, CDS_NORESET = 0x10000000;
        const int ENUM_CURRENT_SETTINGS = -1;

        /// <summary>All virtual monitors from the driver, attached to the desktop or not, in a stable order.</summary>
        public static List<Output> List()
        {
            var list = new List<Output>();
            for (int i = 0; ; i++)
            {
                var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref ad, 0)) break;
                // Identify outputs by the driver's adapter, not by the monitors listed under an output: in
                // Duplicate mode Windows lists the virtual monitors under the PC screen's output.
                if (ad.DeviceID == null || !ad.DeviceID.ToUpperInvariant().Contains(AdapterTag)) continue;
                string monId = null;
                for (int j = 0; ; j++)
                {
                    var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (!EnumDisplayDevices(ad.DeviceName, j, ref md, 0)) break;
                    if (md.DeviceID != null && md.DeviceID.ToUpperInvariant().Contains(MonitorTag)) monId = md.DeviceID;
                }
                var o = new Output { DeviceName = ad.DeviceName, MonitorId = monId, Attached = (ad.StateFlags & 1) != 0 };
                if (o.Attached)
                {
                    var dm = NewDevMode();
                    if (EnumDisplaySettings(ad.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                        o.Bounds = new RECT { Left = dm.dmPositionX, Top = dm.dmPositionY, Right = dm.dmPositionX + dm.dmPelsWidth, Bottom = dm.dmPositionY + dm.dmPelsHeight };
                }
                list.Add(o);
            }
            list.Sort((a, b) => DisplayNumber(a.DeviceName).CompareTo(DisplayNumber(b.DeviceName)));
            return list;
        }

        static int DisplayNumber(string dev)
        {
            int n, i = dev.Length; while (i > 0 && char.IsDigit(dev[i - 1])) i--;
            return int.TryParse(dev.Substring(i), out n) ? n : int.MaxValue;
        }

        /// <summary>True if the output belongs to the virtual display driver.</summary>
        public static bool IsVirtualOutput(string deviceName)
        {
            var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            for (int i = 0; EnumDisplayDevices(null, i, ref ad, 0); i++, ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) })
                if (string.Equals(ad.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return ad.DeviceID != null && ad.DeviceID.ToUpperInvariant().Contains(AdapterTag);
            return false;
        }

        /// <summary>
        /// Any virtual monitor showing something, including one duplicating another screen (Windows' Duplicate
        /// mode applies to every connected display, the virtual ones too).
        /// </summary>
        public static bool AnyVirtualMonitorActive()
        {
            for (int i = 0; ; i++)
            {
                var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref ad, 0)) return false;
                if ((ad.StateFlags & 1) == 0) continue;
                for (int j = 0; ; j++)
                {
                    var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (!EnumDisplayDevices(ad.DeviceName, j, ref md, 0)) break;
                    if ((md.StateFlags & 1) != 0 && md.DeviceID != null && md.DeviceID.ToUpperInvariant().Contains(MonitorTag)) return true;
                }
            }
        }

        public static bool DriverPresent() { return List().Count > 0; }

        static DEVMODE NewDevMode() { var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return dm; }

        /// <summary>
        /// DEVMODE positioned at (x, y) that restates the monitor's current resolution and refresh rate.
        /// (Setting only DM_POSITION makes Windows fall back to the driver's default mode, e.g. 800x600.)
        /// </summary>
        static DEVMODE PositionedMode(string deviceName, int x, int y)
        {
            var cur = NewDevMode();
            var dm = NewDevMode();
            dm.dmFields = DM_POSITION;
            if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref cur) && cur.dmPelsWidth > 0)
            {
                dm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                dm.dmPelsWidth = cur.dmPelsWidth; dm.dmPelsHeight = cur.dmPelsHeight; dm.dmDisplayFrequency = cur.dmDisplayFrequency;
            }
            dm.dmPositionX = x; dm.dmPositionY = y;
            return dm;
        }

        /// <summary>Removes a virtual monitor from the desktop (its windows move to the other screens).</summary>
        public static bool Detach(string deviceName, bool apply = true)
        {
            var dm = NewDevMode();
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;   // zero size = detach
            int r = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (r != 0) LastError = "detach " + deviceName + " -> " + r;
            if (apply) Apply();
            return r == 0;
        }
        public static string LastError;

        /// <summary>Adds a virtual monitor to the desktop to the right of everything else, at width x height.</summary>
        public static bool Attach(string deviceName, int width = DefaultWidth, int height = DefaultHeight, bool apply = true)
        {
            int right = 0, top = 0;
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
            {
                var mi = new MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (Native.GetMonitorInfo(hMon, ref mi) && mi.rcMonitor.Right > right) { right = mi.rcMonitor.Right; top = mi.rcMonitor.Top; }
                return true;
            }, IntPtr.Zero);
            var dm = NewDevMode();
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            dm.dmPositionX = right; dm.dmPositionY = top;
            dm.dmPelsWidth = width; dm.dmPelsHeight = height; dm.dmDisplayFrequency = 60;
            int r = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            int a = apply ? ApplyResult() : 0;
            if (r != 0 || a != 0) LastError = "attach " + deviceName + " at " + right + "," + top + " -> " + r + (apply ? ", apply -> " + a : "");
            return r == 0 && a == 0;
        }

        public static void Apply() { ApplyDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); }
        public static int ApplyResult() { return ApplyDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); }

        /// <summary>
        /// Makes a monitor the Windows primary display (taskbar, Start, new windows). The primary must sit at 0,0,
        /// so every attached monitor is shifted by the same offset.
        /// </summary>
        public static bool SetPrimary(string deviceName)
        {
            var mons = DisplaySetup.ActiveMonitors();
            var p = mons.Find(m => m.DeviceName == deviceName);
            if (p == null) return false;
            if (p.Primary) return true;
            int ox = p.Bounds.Left, oy = p.Bounds.Top;
            foreach (var m in mons)
            {
                var dm = PositionedMode(m.DeviceName, m.Bounds.Left - ox, m.Bounds.Top - oy);
                int flags = CDS_UPDATEREGISTRY | CDS_NORESET | (m.DeviceName == deviceName ? 0x10 /*CDS_SET_PRIMARY*/ : 0);
                ChangeDisplaySettingsEx(m.DeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            }
            Apply();
            return true;
        }

        /// <summary>Sets a monitor's refresh rate (resolution and position stay). Returns the rate it then runs at.</summary>
        public static int SetRefresh(string deviceName, int hz)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm)) return 0;
            if (dm.dmDisplayFrequency == hz) return hz;
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY; dm.dmDisplayFrequency = hz;
            ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            System.Threading.Thread.Sleep(500);   // a refused rate reads back for a moment before Windows drops it
            dm = NewDevMode();
            return EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 0;
        }

        /// <summary>Sets an attached monitor's resolution, keeping its position (queued until Apply()).</summary>
        public static bool SetMode(string deviceName, int width, int height)
        {
            var cur = NewDevMode();
            if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref cur)) return false;
            var dm = NewDevMode();
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            dm.dmPositionX = cur.dmPositionX; dm.dmPositionY = cur.dmPositionY;
            dm.dmPelsWidth = width; dm.dmPelsHeight = height; dm.dmDisplayFrequency = 60;
            return ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero) == 0;
        }

        /// <summary>Moves an attached monitor to (x, y) on the Windows desktop (queued until Apply()).</summary>
        public static bool SetPosition(string deviceName, int x, int y)
        {
            var dm = PositionedMode(deviceName, x, y);
            return ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero) == 0;
        }

        /// <summary>Detaches every virtual monitor (viewer exit, after install, recovery).</summary>
        public static int DetachAll()
        {
            int n = 0;
            foreach (var o in List()) if (o.Attached) { Detach(o.DeviceName, false); n++; }
            if (n > 0) Apply();
            return n;
        }
    }
}
