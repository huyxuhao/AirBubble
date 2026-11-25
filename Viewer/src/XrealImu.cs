using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XrealDesktop.PcViewer
{
    public struct Quat
    {
        public double W, X, Y, Z;
        public Quat(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }
        public static readonly Quat Identity = new Quat(1, 0, 0, 0);

        public static Quat operator *(Quat a, Quat b)
        {
            return new Quat(
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);
        }
        public Quat Conj() { return new Quat(W, -X, -Y, -Z); }
        public Quat Normalized() { double n = Math.Sqrt(W * W + X * X + Y * Y + Z * Z); return n < 1e-12 ? Identity : new Quat(W / n, X / n, Y / n, Z / n); }

        public static Quat AxisAngle(double ax, double ay, double az, double rad)
        {
            double s = Math.Sin(rad / 2);
            return new Quat(Math.Cos(rad / 2), ax * s, ay * s, az * s);
        }
        /// <summary>Rotation by a rotation vector (axis * angle, radians).</summary>
        public static Quat FromRotVec(double x, double y, double z)
        {
            double a = Math.Sqrt(x * x + y * y + z * z);
            if (a < 1e-12) return Identity;
            return AxisAngle(x / a, y / a, z / a, a);
        }
        public void Rotate(double x, double y, double z, out double ox, out double oy, out double oz)
        {
            var r = this * new Quat(0, x, y, z) * Conj();
            ox = r.X; oy = r.Y; oz = r.Z;
        }
        /// <summary>Heading (rotation about +Y) of this orientation's forward (-Z) vector, radians.</summary>
        public double Yaw()
        {
            double fx, fy, fz; Rotate(0, 0, -1, out fx, out fy, out fz);
            return Math.Atan2(-fx, -fz);
        }
        /// <summary>Column-major 4x4 rotation matrix for glMultMatrixd.</summary>
        public double[] ToGlMatrix()
        {
            double w = W, x = X, y = Y, z = Z;
            return new double[] {
                1 - 2*(y*y + z*z), 2*(x*y + w*z),     2*(x*z - w*y),     0,
                2*(x*y - w*z),     1 - 2*(x*x + z*z), 2*(y*z + w*x),     0,
                2*(x*z + w*y),     2*(y*z - w*x),     1 - 2*(x*x + y*y), 0,
                0, 0, 0, 1 };
        }
    }

    /// <summary>
    /// Reads the XREAL Air IMU over USB HID and fuses it into a head orientation.
    ///
    /// Axis mapping, measured on this unit with ImuTest (guided runs):
    ///   sensor Z = yaw   (turn left  = +)      -> GL +Y
    ///   sensor X = pitch (look up    = -)      -> GL -X
    ///   sensor Y = roll  (tilt right = -)      -> GL +Z
    /// i.e. GL vector = (-sx, sz, sy). GL frame: +X right, +Y up, -Z forward.
    ///
    /// Fusion is a Mahony complementary filter: gyro integration, with the accelerometer pulling
    /// pitch/roll back to level. Yaw cannot be corrected (no usable magnetometer), so the gyro bias
    /// is re-estimated whenever the head is held still, and the user can recenter.
    /// </summary>
    public sealed class XrealImu : IDisposable
    {
        // ---------------- HID interop ----------------
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr di, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);

        static readonly uint[] CrcTable = BuildCrc();
        static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
            return t;
        }
        static byte[] BuildCommand(byte cmd, byte[] data, int outLen)
        {
            int packetLen = 3 + data.Length;
            var p = new byte[Math.Max(outLen, 1 + 5 + packetLen)];
            p[1] = 0xAA; p[6] = (byte)packetLen; p[7] = (byte)(packetLen >> 8); p[8] = cmd;
            Array.Copy(data, 0, p, 9, data.Length);
            uint c = 0xFFFFFFFF;
            for (int i = 0; i < packetLen; i++) c = CrcTable[(c ^ p[6 + i]) & 0xFF] ^ (c >> 8);
            c ^= 0xFFFFFFFF;
            p[2] = (byte)c; p[3] = (byte)(c >> 8); p[4] = (byte)(c >> 16); p[5] = (byte)(c >> 24);
            return p;
        }
        static int I24(byte[] b, int o) { int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16); return (v << 8) >> 8; }

        // ---------------- tuning ----------------
        const double Deg = Math.PI / 180;
        public double Kp = 0.4;           // accel correction gain (steady state)
        public double KpStartup = 8.0;    // fast leveling right after calibration
        public double Ki = 0.002;         // pitch/roll bias integral
        public double AccelTrustMaxDps = 20; // head speed at which the accelerometer stops being believed (0 trust)
        const double StillAccelTol = 0.04;// |a| within 1 +- this (g)

        // ---------------- gyro bias estimation (the main source of yaw drift) ----------------
        // Measured on this Air: in genuinely still moments while worn, the gyro's noise is 0.3-0.7 dps (sd over
        // 1-2 s) and its offset wanders ~0.1 dps within a minute; slow head turns of 1-3 dps are common.
        // So bias is only learnt from whole 1 s blocks that are provably still (low spread, steady gravity,
        // mean close to the current estimate) - never from single samples, which would absorb slow head turns.
        const double BlockSec = 1.0;
        const double StillSdDps = 1.0;      // per-axis sd within a block
        const double StillAccSd = 0.015;    // sd of |a| within a block (g)
        const double MaxJumpDps = 0.5;      // block mean must be within this of the predicted bias
        const double Forget = 0.97;         // per accepted block: ~30 s of still time of memory
        double m_BlkT, m_BlkN, m_BlkTemp;
        readonly double[] m_BlkS = new double[3], m_BlkSS = new double[3];
        double m_BlkA, m_BlkAA;
        double[] m_CalPrev;                 // previous accepted block mean during initial calibration
        // Temperature-aware bias model: forgetting least squares per axis, bias(T) = mean + slope*(T - meanT).
        double m_Sw, m_St, m_Stt;
        readonly double[] m_Sb = new double[3], m_Stb = new double[3];
        double m_Temp = 25;
        public double TempC { get { return m_Temp; } }
        public int BiasBlocks;              // accepted still blocks (diagnostics)

        // ---------------- state ----------------
        readonly object m_Lock = new object();
        FileStream m_Stream;
        Thread m_Thread;
        volatile bool m_Run;

        Quat m_Q = Quat.Identity;           // body -> world
        double m_Wx, m_Wy, m_Wz;            // latest bias-corrected body rate (GL frame, rad/s)
        double m_Bx, m_By, m_Bz;            // gyro bias, raw sensor frame (deg/s)
        double m_Ix, m_Iy, m_Iz;            // Mahony integral term
        bool m_Calibrated;
        double m_SinceCal;
        bool m_PendingCalEvent;              // saved calibration: tell the viewer once the head is levelled
        ulong m_LastTs;

        public string Status = "starting";
        public string DeviceName = "";
        public volatile bool Disconnected;
        public long Packets;
        public double SampleRate;
        public bool Calibrated { get { return m_Calibrated; } }
        public double BiasX { get { return m_Bx; } } public double BiasY { get { return m_By; } } public double BiasZ { get { return m_Bz; } }
        public event Action CalibrationDone;

        public bool Start()
        {
            var dev = XrealDevice.Find();
            string path = dev == null ? null : dev.ImuPath;
            if (path == null) { Status = "XREAL glasses not found (USB)"; return false; }
            DeviceName = dev.Name;
            var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { Status = "cannot open glasses IMU (error " + Marshal.GetLastWin32Error() + ")"; return false; }
            m_Stream = new FileStream(h, FileAccess.ReadWrite, 1, false);
            var cmd = BuildCommand(0x19, new byte[] { 0x01 }, 65);   // START_IMU_DATA
            m_Stream.Write(cmd, 0, 65); m_Stream.Flush();
            m_Run = true;
            m_Thread = new Thread(ReadLoop) { IsBackground = true, Name = "imu", Priority = ThreadPriority.AboveNormal };
            m_Thread.Start();
            Status = "calibrating: hold still";
            LoadBias();
            LoadDisplayCal();
            return true;
        }

        static string BiasFile { get { return Path.Combine(Settings.Dir, "imu_bias.ini"); } }

        public void UseSavedCalibration() { LoadBias(); }

        void LoadBias()
        {
            try
            {
                var p = File.ReadAllText(BiasFile).Split(',');
                if (p.Length < 9 || p[0] != DeviceName) return;
                var c = System.Globalization.CultureInfo.InvariantCulture;
                // keep the old knowledge but let fresh observations dominate quickly
                double w = Math.Min(double.Parse(p[1], c), 5);
                double mt = double.Parse(p[2], c), vt = double.Parse(p[3], c);
                m_Sw = w; m_St = w * mt; m_Stt = w * (vt + mt * mt);
                for (int i = 0; i < 3; i++)
                {
                    double mb = double.Parse(p[4 + i], c), slope = double.Parse(p[7 + Math.Min(i, 1)], c);
                    if (p.Length >= 10) slope = double.Parse(p[7 + i], c);
                    m_Sb[i] = w * mb; m_Stb[i] = w * (mb * mt + slope * vt);
                }
                m_Bx = PredictBias(0, mt); m_By = PredictBias(1, mt); m_Bz = PredictBias(2, mt);
                // seed the median with a few copies of the saved estimate so it starts there, yet 3-4 fresh still
                // seconds can overrule it
                for (int k = 0; k < 3; k++) m_Recent.Add(new[] { mt, m_Bx, m_By, m_Bz });
                m_Calibrated = true; m_SinceCal = 0; m_PendingCalEvent = true; Status = "tracking (saved calibration)";
            }
            catch { }
        }

        public void SaveBias()
        {
            if (m_Sw < 1) return;
            try
            {
                double mt = m_St / m_Sw, vt = Math.Max(0, m_Stt / m_Sw - mt * mt);
                var c = System.Globalization.CultureInfo.InvariantCulture;
                var parts = new List<string> { DeviceName, m_Sw.ToString("F2", c), mt.ToString("F3", c), vt.ToString("F4", c) };
                for (int i = 0; i < 3; i++) parts.Add(PredictBias(i, mt).ToString("F4", c));   // median-based level
                for (int i = 0; i < 3; i++) parts.Add(Slope(i).ToString("F5", c));
                Directory.CreateDirectory(Settings.Dir);
                File.WriteAllText(BiasFile, string.Join(",", parts));
            }
            catch { }
        }

        double Slope(int i)
        {
            if (m_Sw < 1) return 0;
            double mt = m_St / m_Sw, var = m_Stt / m_Sw - mt * mt;
            if (var < 0.25) return 0;                       // need > ~0.5 C of temperature spread
            double cov = m_Stb[i] / m_Sw - mt * (m_Sb[i] / m_Sw);
            return Math.Max(-0.08, Math.Min(0.08, cov / var));
        }

        // recent accepted still blocks: (temperature, bias x, y, z)
        readonly List<double[]> m_Recent = new List<double[]>();
        const int RecentMax = 25;

        /// <summary>
        /// Bias at 'temp': the MEDIAN of the recent still blocks, each shifted to 'temp' with the learnt
        /// temperature slope. A median ignores the odd block that was really a slow, steady head turn.
        /// </summary>
        double PredictBias(int i, double temp)
        {
            if (m_Recent.Count == 0) { if (m_Sw <= 0) return 0; return m_Sb[i] / m_Sw + Slope(i) * (temp - m_St / m_Sw); }
            double sl = Slope(i);
            var v = new List<double>(m_Recent.Count);
            foreach (var r in m_Recent) v.Add(r[1 + i] + sl * (temp - r[0]));
            v.Sort();
            int n = v.Count;
            return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
        }

        void ObserveBias(double temp, double[] mean)
        {
            m_Recent.Add(new[] { temp, mean[0], mean[1], mean[2] });
            if (m_Recent.Count > RecentMax) m_Recent.RemoveAt(0);
            m_Sw = m_Sw * Forget + 1; m_St = m_St * Forget + temp; m_Stt = m_Stt * Forget + temp * temp;
            for (int i = 0; i < 3; i++) { m_Sb[i] = m_Sb[i] * Forget + mean[i]; m_Stb[i] = m_Stb[i] * Forget + temp * mean[i]; }
            BiasBlocks++;
        }

        /// <summary>Accumulates 1 s blocks and turns provably-still ones into bias observations.</summary>
        void BiasBlock(double gx, double gy, double gz, double amag, double temp, double dt, ref bool fireCal)
        {
            m_BlkS[0] += gx; m_BlkS[1] += gy; m_BlkS[2] += gz;
            m_BlkSS[0] += gx * gx; m_BlkSS[1] += gy * gy; m_BlkSS[2] += gz * gz;
            m_BlkA += amag; m_BlkAA += amag * amag; m_BlkTemp += temp; m_BlkN++; m_BlkT += dt;
            if (m_BlkT < BlockSec) return;
            double n = m_BlkN; var mean = new double[3]; bool still = true;
            for (int i = 0; i < 3; i++)
            {
                mean[i] = m_BlkS[i] / n;
                double sd = Math.Sqrt(Math.Max(0, m_BlkSS[i] / n - mean[i] * mean[i]));
                if (sd > StillSdDps) still = false;
            }
            double ma = m_BlkA / n, sa = Math.Sqrt(Math.Max(0, m_BlkAA / n - ma * ma)), bt = m_BlkTemp / n;
            if (sa > StillAccSd || Math.Abs(ma - 1) > StillAccelTol) still = false;
            m_BlkT = m_BlkN = m_BlkA = m_BlkAA = m_BlkTemp = 0;
            for (int i = 0; i < 3; i++) m_BlkS[i] = m_BlkSS[i] = 0;

            if (!m_Calibrated)
            {
                // Initial calibration: two consecutive still blocks that agree with each other.
                if (!still) { m_CalPrev = null; return; }
                if (m_CalPrev != null && Math.Abs(mean[0] - m_CalPrev[0]) < 0.35 && Math.Abs(mean[1] - m_CalPrev[1]) < 0.35 && Math.Abs(mean[2] - m_CalPrev[2]) < 0.35)
                {
                    var avg = new double[3];
                    for (int i = 0; i < 3; i++) avg[i] = (mean[i] + m_CalPrev[i]) / 2;
                    ObserveBias(bt, avg);
                    m_Bx = avg[0]; m_By = avg[1]; m_Bz = avg[2];
                    m_Ix = m_Iy = m_Iz = 0;
                    m_Calibrated = true; m_SinceCal = 0; fireCal = true; Status = "tracking";
                    m_CalPrev = null;
                }
                else m_CalPrev = mean;
                return;
            }
            if (!still) return;
            // while the estimate is young, any provably-still block may correct it; afterwards only nearby ones
            double jump = m_Recent.Count < 6 ? 2.0 : MaxJumpDps;
            if (Math.Abs(mean[0] - m_Bx) > jump || Math.Abs(mean[1] - m_By) > jump || Math.Abs(mean[2] - m_Bz) > jump) return;
            ObserveBias(bt, mean);
        }

        public void Dispose()
        {
            lock (m_Lock) SaveBias();
            m_Run = false;
            try { if (m_Stream != null) m_Stream.Dispose(); } catch { }
        }

        /// <summary>Current orientation, predicted 'aheadSec' into the future using the current rate.</summary>
        public Quat GetPose(double aheadSec)
        {
            Quat q; double wx, wy, wz;
            lock (m_Lock) { q = m_Q; wx = m_Wx; wy = m_Wy; wz = m_Wz; }
            if (aheadSec > 0) q = (q * Quat.FromRotVec(wx * aheadSec, wy * aheadSec, wz * aheadSec)).Normalized();
            return q;
        }

        /// <summary>Forces a fresh gyro bias calibration (head must be still).</summary>
        public void Recalibrate()
        {
            lock (m_Lock) { m_Calibrated = false; m_CalPrev = null; Status = "calibrating: hold still"; }
        }

        // ---------------- factory calibration ----------------
        // Every pair of glasses stores its own calibration (JSON, ~39 kB). It is fetched in 56-byte segments in between
        // the IMU packets: 0x14 = length, 0x15 = next segment. (0x16-0x18 allocate/write/free it and are never sent.)
        /// <summary>Focal lengths of one eye's 1920x1080 display in pixels (0 until read; they differ: pixels are not square).
        /// These fix how many pixels a degree of head turn is - guess them wrong and every screen slides as you turn.</summary>
        public double FocalX, FocalY;
        int m_CalLen, m_CalWait, m_CalTries; MemoryStream m_Cal; bool m_CalDone;

        // The values never change for a pair of glasses, and the glasses do not always answer (seen: their command
        // channel going silent after two display-mode switches in a row), so the first good read is kept on disk and
        // they are not asked again.
        static string DisplayCalFile { get { return Path.Combine(Settings.Dir, "display_cal.ini"); } }
        void LoadDisplayCal()
        {
            try
            {
                var p = File.ReadAllText(DisplayCalFile).Trim().Split(','); var c = System.Globalization.CultureInfo.InvariantCulture;
                if (p.Length != 3 || p[0] != DeviceName) return;
                FocalX = double.Parse(p[1], c); FocalY = double.Parse(p[2], c); m_CalDone = true;
                Log.W(string.Format(c, "Glasses calibration (saved): field of view {0:F2} x {1:F2} deg", 2 * Math.Atan(960 / FocalX) / Deg, 2 * Math.Atan(540 / FocalY) / Deg));
            }
            catch { }
        }
        void SaveDisplayCal()
        {
            try { File.WriteAllText(DisplayCalFile, string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2}", DeviceName, FocalX, FocalY)); } catch { }
        }

        void Send(byte cmd) { var c = BuildCommand(cmd, new byte[0], 65); m_Stream.Write(c, 0, 65); m_Stream.Flush(); }

        void OnReply(byte[] buf)
        {
            m_CalWait = 0;
            int len = (buf[6] | (buf[7] << 8)) - 3;
            if (buf[8] == 0x14 && len >= 4 && m_Cal == null)
            {
                m_CalLen = BitConverter.ToInt32(buf, 9);
                if (m_CalLen > 0 && m_CalLen < 1 << 20) { m_Cal = new MemoryStream(); Send(0x15); }
            }
            else if (buf[8] == 0x15 && m_Cal != null && len > 0 && len <= 56)
            {
                m_Cal.Write(buf, 9, len);
                if (m_Cal.Length < m_CalLen) { Send(0x15); return; }
                double fx, fy;
                bool ok = ParseDisplayCal(System.Text.Encoding.UTF8.GetString(m_Cal.ToArray()), out fx, out fy);
                m_Cal = null; m_CalDone = true;
                if (ok) { FocalX = fx; FocalY = fy; SaveDisplayCal(); }
                Log.W(ok ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Glasses calibration: focal {0:F1} x {1:F1} px -> field of view {2:F2} x {3:F2} deg",
                               fx, fy, 2 * Math.Atan(960 / fx) / Deg, 2 * Math.Atan(540 / fy) / Deg)
                         : "Glasses calibration: no usable display data, using the Field of view setting");
            }
        }

        /// <summary>Mean focal lengths of the two eye displays ("k_left_display": [fx, 0, cx, 0, fy, cy, 0, 0, 1]).</summary>
        public static bool ParseDisplayCal(string json, out double fx, out double fy)
        {
            fx = fy = 0;
            var ms = System.Text.RegularExpressions.Regex.Matches(json, "\"k_(?:left|right)_display\"\\s*:\\s*\\[([^\\]]+)\\]");
            foreach (System.Text.RegularExpressions.Match m in ms)
            {
                var v = m.Groups[1].Value.Split(',');
                double x, y; var c = System.Globalization.CultureInfo.InvariantCulture; var f = System.Globalization.NumberStyles.Float;
                if (v.Length < 5 || !double.TryParse(v[0], f, c, out x) || !double.TryParse(v[4], f, c, out y)) return false;
                fx += x / ms.Count; fy += y / ms.Count;
            }
            // 900..4500 px = 12..62 deg: anything else is not a field of view these glasses could have
            return fx > 900 && fx < 4500 && fy > 900 && fy < 4500;
        }

        void ReadLoop()
        {
            var buf = new byte[65];
            var rateWatch = System.Diagnostics.Stopwatch.StartNew(); long rateCount = 0;
            while (m_Run)
            {
                int n;
                try { n = m_Stream.Read(buf, 0, 65); }
                catch (Exception e) { if (m_Run) { Status = "glasses disconnected: " + e.Message; Disconnected = true; } return; }
                if (n == 65 && buf[1] == 0xAA) { try { OnReply(buf); } catch { } continue; }
                // ask for the calibration once the stream runs (a request sent right behind the start command is lost),
                // and again if the answers dry up - a few times, then live with the Field of view setting
                if (!m_CalDone && ++m_CalWait > 1000)
                {
                    m_CalWait = 0; m_Cal = null;
                    if (++m_CalTries > 3) { m_CalDone = true; Log.W("Glasses calibration: no answer, using the Field of view setting"); }
                    else try { Send(0x14); } catch { }
                }
                if (n < 65 || buf[1] != 0x01 || buf[2] != 0x02) continue;
                const int o = 1;
                ulong ts = BitConverter.ToUInt64(buf, o + 4);
                double gMul = BitConverter.ToInt16(buf, o + 12), gDiv = BitConverter.ToInt32(buf, o + 14);
                double aMul = BitConverter.ToInt16(buf, o + 27), aDiv = BitConverter.ToInt32(buf, o + 29);
                if (gDiv == 0 || aDiv == 0) continue;
                double gx = I24(buf, o + 18) * gMul / gDiv, gy = I24(buf, o + 21) * gMul / gDiv, gz = I24(buf, o + 24) * gMul / gDiv;
                double ax = I24(buf, o + 33) * aMul / aDiv, ay = I24(buf, o + 36) * aMul / aDiv, az = I24(buf, o + 39) * aMul / aDiv;
                m_Temp = BitConverter.ToInt16(buf, o + 2) / 326.8 + 25.0;

                double dt = m_LastTs != 0 && ts > m_LastTs ? (ts - m_LastTs) * 1e-9 : 0.001;
                m_LastTs = ts;
                if (dt > 0.05) dt = 0.001;
                Packets++; rateCount++;
                if (rateWatch.ElapsedMilliseconds >= 1000) { SampleRate = rateCount * 1000.0 / rateWatch.ElapsedMilliseconds; rateCount = 0; rateWatch.Restart(); }
                Update(gx, gy, gz, ax, ay, az, dt);
            }
        }

        /// <summary>Feeds one recorded sample (raw sensor units) through the tracker - used by --replay.</summary>
        public void Feed(double gx, double gy, double gz, double ax, double ay, double az, double dt, double temp)
        {
            m_Temp = temp; Update(gx, gy, gz, ax, ay, az, dt);
        }

        void Update(double gx, double gy, double gz, double ax, double ay, double az, double dt)
        {
            bool fireCal = false;
            lock (m_Lock)
            {
                double amag = Math.Sqrt(ax * ax + ay * ay + az * az);
                bool accelStill = Math.Abs(amag - 1) < StillAccelTol;

                BiasBlock(gx, gy, gz, amag, m_Temp, dt, ref fireCal);
                if (m_PendingCalEvent && m_SinceCal > 1.0) { m_PendingCalEvent = false; fireCal = true; }
                if (m_Calibrated && m_Sw > 0)
                {
                    // follow the temperature model smoothly between still periods
                    double k = Math.Min(1, dt * 0.5);
                    m_Bx += (PredictBias(0, m_Temp) - m_Bx) * k; m_By += (PredictBias(1, m_Temp) - m_By) * k; m_Bz += (PredictBias(2, m_Temp) - m_Bz) * k;
                }

                // Body rate in GL frame (rad/s).
                double wx = -(gx - m_Bx) * Deg, wy = (gz - m_Bz) * Deg, wz = (gy - m_By) * Deg;
                // Measured "up" in GL body frame.
                double ux = -ax, uy = az, uz = ay;

                m_SinceCal += dt;
                if (amag > 0.5 && amag < 1.5)
                {
                    double inv = 1.0 / amag; ux *= inv; uy *= inv; uz *= inv;
                    // Estimated up in body frame: R^T * (0,1,0).
                    double vx, vy, vz; m_Q.Conj().Rotate(0, 1, 0, out vx, out vy, out vz);
                    double ex = uy * vz - uz * vy, ey = uz * vx - ux * vz, ez = ux * vy - uy * vx;
                    bool startup = !m_Calibrated || m_SinceCal < 1.0;
                    double kp = startup ? KpStartup : Kp;
                    // Weight down the correction when linear acceleration is significant.
                    double trust = Math.Max(0, 1 - Math.Abs(amag - 1) * 5);
                    // A turning head swings the sensor around the neck, and the accelerometer feels that swing on top
                    // of gravity without |a| changing much. Levelling towards it tilts the picture while you turn and
                    // lets it creep back afterwards (--wiggle-test: 0.6-1.9 deg/s), so gravity only counts while the head
                    // is nearly still; a turn is far too short for the gyro alone to lose the horizon.
                    if (!startup) trust *= Math.Max(0, 1 - Math.Sqrt(wx * wx + wy * wy + wz * wz) / Deg / AccelTrustMaxDps);
                    if (m_Calibrated) { m_Ix += Ki * ex * dt * trust; m_Iy += Ki * ey * dt * trust; m_Iz += Ki * ez * dt * trust; }
                    wx += (kp * ex + m_Ix) * trust; wy += (kp * ey + m_Iy) * trust; wz += (kp * ez + m_Iz) * trust;
                }
                if (!m_Calibrated) { wx = wy = wz = 0; double vx2, vy2, vz2; m_Q.Conj().Rotate(0, 1, 0, out vx2, out vy2, out vz2); LevelTowards(ux, uy, uz, vx2, vy2, vz2, dt); }
                else m_Q = (m_Q * Quat.FromRotVec(wx * dt, wy * dt, wz * dt)).Normalized();
                m_Wx = -(gx - m_Bx) * Deg; m_Wy = (gz - m_Bz) * Deg; m_Wz = (gy - m_By) * Deg;
                if (!m_Calibrated) { m_Wx = m_Wy = m_Wz = 0; }
            }
            if (fireCal && CalibrationDone != null) CalibrationDone();
        }

        // During calibration: only level the head (accelerometer), no gyro integration.
        void LevelTowards(double ux, double uy, double uz, double vx, double vy, double vz, double dt)
        {
            double ex = uy * vz - uz * vy, ey = uz * vx - ux * vz, ez = ux * vy - uy * vx;
            m_Q = (m_Q * Quat.FromRotVec(ex * KpStartup * dt, ey * KpStartup * dt, ez * KpStartup * dt)).Normalized();
        }
    }
}
