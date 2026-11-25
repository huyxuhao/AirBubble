using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// The glasses' control MCU (HID interface 4 on Air / Air 2 / Air 2 Pro, 0 on Air 2 Ultra).
    /// Only two messages are ever sent: read display mode (0x07) and write display mode (0x08).
    /// Protocol from the open-source xrealAirLinuxDriver (device_mcu.c):
    ///   [0xFD][crc32 LE][len LE = 17+n][timestamp u64 = 0][msgid u16][reserved 5][data n]
    ///   crc covers 'len' bytes starting at the length field. Reply: data[0] = status (0 = ok), data[1..] = payload.
    /// </summary>
    public static class XrealMcu
    {
        public const byte MODE_2D_1080_60 = 0x1, MODE_SBS_3840_60 = 0x3, MODE_2D_1080_90 = 0xA, MODE_2D_1080_120 = 0xB;   // device_mcu.h
        /// <summary>The 2D mode for a refresh rate. The glasses only take a 90/120 Hz signal after being put in that mode.</summary>
        public static byte Mode2D(int hz) { return hz >= 120 ? MODE_2D_1080_120 : hz >= 90 ? MODE_2D_1080_90 : MODE_2D_1080_60; }
        const ushort MSG_R_DISP_MODE = 0x07, MSG_W_DISP_MODE = 0x08;
        const double ModeGapSec = 15;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);

        static readonly uint[] CrcTable = BuildCrc();
        static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
            return t;
        }

        static byte[] Build(ushort msgid, byte[] data)
        {
            int n = data == null ? 0 : data.Length;
            int len = 17 + n;
            var p = new byte[65];               // [0] = HID report id 0, packet from [1]
            const int o = 1;
            p[o] = 0xFD;
            p[o + 5] = (byte)len; p[o + 6] = (byte)(len >> 8);
            // timestamp (o+7..o+14) = 0
            p[o + 15] = (byte)msgid; p[o + 16] = (byte)(msgid >> 8);
            if (n > 0) Array.Copy(data, 0, p, o + 22, n);
            uint c = 0xFFFFFFFF;
            for (int i = 0; i < len; i++) c = CrcTable[(c ^ p[o + 5 + i]) & 0xFF] ^ (c >> 8);
            c ^= 0xFFFFFFFF;
            p[o + 1] = (byte)c; p[o + 2] = (byte)(c >> 8); p[o + 3] = (byte)(c >> 16); p[o + 4] = (byte)(c >> 24);
            return p;
        }

        static FileStream Open(out string error)
        {
            error = null;
            var dev = XrealDevice.Find();
            if (dev == null || dev.McuPath == null) { error = "glasses control interface not found"; return null; }
            var h = CreateFile(dev.McuPath, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
            if (h.IsInvalid) { error = "cannot open control interface (error " + Marshal.GetLastWin32Error() + ")"; return null; }
            return new FileStream(h, FileAccess.ReadWrite, 1, true);
        }

        /// <summary>Sends one message and waits for the matching reply. Returns the reply payload (after the status byte) or null.</summary>
        static byte[] Transact(FileStream fs, ushort msgid, byte[] data, int timeoutMs, out string error)
        {
            error = null;
            var pkt = Build(msgid, data);
            fs.Write(pkt, 0, 65);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buf = new byte[65];
            while (DateTime.UtcNow < deadline)
            {
                var ar = fs.BeginRead(buf, 0, 65, null, null);
                int ms = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (!ar.AsyncWaitHandle.WaitOne(ms)) { error = "no reply"; return null; }   // pending read is abandoned with the handle
                int n = fs.EndRead(ar);
                if (n < 24 || buf[1] != 0xFD) continue;
                ushort id = (ushort)(buf[1 + 15] | (buf[1 + 16] << 8));
                if (id != msgid) continue;                       // heartbeats, button events, etc.
                if (buf[1 + 22] != 0) { error = "status " + buf[1 + 22]; return null; }
                var payload = new byte[42 - 1];
                Array.Copy(buf, 1 + 23, payload, 0, payload.Length);
                return payload;
            }
            error = "no reply";
            return null;
        }

        /// <summary>Reads the current display mode (1 = 2D 1920x1080, 3 = SBS 3840x1080, ...). -1 on failure.</summary>
        public static int ReadDisplayMode(out string error)
        {
            using (var fs = Open(out error))
            {
                if (fs == null) return -1;
                var r = Transact(fs, MSG_R_DISP_MODE, null, 1500, out error);
                return r == null ? -1 : r[0];
            }
        }

        /// <summary>Switches the glasses' display mode. Windows sees the display change resolution a moment later.</summary>
        public static bool WriteDisplayMode(byte mode, out string error)
        {
            if (mode != MODE_2D_1080_60 && mode != MODE_SBS_3840_60 && mode != MODE_2D_1080_90 && mode != MODE_2D_1080_120) { error = "unsupported mode"; return false; }
            // ponytail: fixed pause. A second mode switch 6 s after the first left the glasses deaf to every command
            // until replugged (seen once: quit, then start right away), so switches are kept ModeGapSec apart, across
            // runs. Shorten it if a shorter gap proves safe.
            string stamp = Path.Combine(Settings.Dir, "mode_switch.stamp");
            try { double age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp)).TotalSeconds; if (age >= 0 && age < ModeGapSec) Thread.Sleep((int)((ModeGapSec - age) * 1000)); } catch { }
            using (var fs = Open(out error))
            {
                if (fs == null) return false;
                bool ok = Transact(fs, MSG_W_DISP_MODE, new[] { mode }, 3000, out error) != null;
                if (ok) try { File.WriteAllText(stamp, ""); } catch { }
                return ok;
            }
        }
    }
}
