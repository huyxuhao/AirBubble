using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using XrealDesktop.Host;

namespace XrealDesktop.PcViewer
{
    static class Log
    {
        static readonly object s_Lock = new object();
        static string s_Path;
        public static void Init(string path) { s_Path = path; try { File.WriteAllText(path, ""); } catch { } }
        public static void W(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            Console.WriteLine(line);
            lock (s_Lock) { try { if (s_Path != null) File.AppendAllText(s_Path, line + "\r\n"); } catch { } }
        }
    }

    static class Program
    {
        public const string QuitEventName = "XrealPcViewer.Quit";
        public static bool FixedHead;
        public static bool VideoLoad;
        public static bool CalTest;      // --cal-test (with --selftest): 3D calibration readbacks
        public static bool BubbleTest;   // --bubble-test (with --selftest): Bubble readbacks + pop
        public static bool AiTest;       // --ai-test "question" (with --selftest): ask Bubble by text, read back his answer
        public static string AiTestQuestion;
        public static double FixedPitch;   // degrees, with --fixed-head (self-test looks down at the dock)
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            double selfTest = 0;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--selftest" && i + 1 < args.Length) selfTest = double.Parse(args[i + 1], CultureInfo.InvariantCulture);

            if (Array.IndexOf(args, "--quit") >= 0)
            {
                // Ask a running instance to quit cleanly (restores the display mode). Used by the installer too.
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(QuitEventName, out ev)) { ev.Set(); return 0; }
                return 1;
            }

            if (Array.IndexOf(args, "--detach-virtual") >= 0) { VirtualScreens.DetachAll(); return 0; }
            if (Array.IndexOf(args, "--guard") >= 0) return Guard.Run();
            int pt = Array.IndexOf(args, "--primary-test");
            if (pt >= 0 && pt + 1 < args.Length)
            {
                // Diagnostics: attach N virtual screens (extended), then try to make the first one primary.
                AttachConsole(-1);
                int n = int.Parse(args[pt + 1]);
                var outs = VirtualScreens.List();
                for (int i = 0; i < n && i < outs.Count; i++) if (!outs[i].Attached) Console.WriteLine("attach " + outs[i].DeviceName + ": " + VirtualScreens.Attach(outs[i].DeviceName) + " " + VirtualScreens.LastError);
                Thread.Sleep(1500);
                Console.WriteLine("before: " + DisplayConfig.Describe());
                outs = VirtualScreens.List();
                var first = outs.Find(o => o.Attached);
                if (first != null) Console.WriteLine("primary " + first.DeviceName + ": " + DisplayConfig.EnsurePrimary(first.DeviceName, Console.WriteLine));
                Console.WriteLine("after:  " + DisplayConfig.Describe());
                return 0;
            }
            if (Array.IndexOf(args, "--restore-desktop") >= 0)
            {
                // Diagnostics / recovery: normal PC desktop (virtual screens off, PC screen primary, glasses duplicating it).
                AttachConsole(-1);
                Console.WriteLine("before: " + DisplayConfig.Describe());
                DisplayConfig.RestorePcDesktop(null, Console.WriteLine);
                Console.WriteLine("after:  " + DisplayConfig.Describe());
                return DisplayConfig.PcDesktopOk() ? 0 : 1;
            }
            int st = Array.IndexOf(args, "--stab-test");
            if (st >= 0 && st + 1 < args.Length)
            {
                // Replays a recording at 60 Hz render rate through the tracker + stabiliser at each level and
                // measures: jitter while holding still (frame-to-frame view motion) and lag during real turns.
                AttachConsole(-1);
                var lines = System.IO.File.ReadAllLines(args[st + 1]);
                var hdr = new List<string>(lines[0].Split(','));
                int it = hdr.IndexOf("t_s"), igx = hdr.IndexOf("gx_dps"), iax = hdr.IndexOf("ax_g"), itemp = hdr.IndexOf("tempC");
                var ci = CultureInfo.InvariantCulture;
                // still windows / turn windows of the guided drift recording
                Func<double, bool> still = t => (t >= 14 && t < 27) || (t >= 72 && t < 86);
                Func<double, bool> turning = t => (t >= 28 && t < 34) || (t >= 40 && t < 46);
                foreach (Stabilizer.Level lvl in Enum.GetValues(typeof(Stabilizer.Level)))
                {
                    var imu = new XrealImu { DeviceName = "stabtest" };
                    var stab = new Stabilizer { Mode = lvl };
                    double prevT = -1, nextFrame = 0, sumSq = 0, n = 0, maxLag = 0, peak = 0; Quat lastShown = Quat.Identity; bool have = false;
                    for (int li = 1; li < lines.Length; li++)
                    {
                        var c = lines[li].Split(',');
                        double t = double.Parse(c[it], ci), dt = prevT < 0 ? 0.001 : Math.Max(0.0001, Math.Min(0.05, t - prevT)); prevT = t;
                        imu.Feed(double.Parse(c[igx], ci), double.Parse(c[igx + 1], ci), double.Parse(c[igx + 2], ci),
                                 double.Parse(c[iax], ci), double.Parse(c[iax + 1], ci), double.Parse(c[iax + 2], ci), dt,
                                 itemp >= 0 ? double.Parse(c[itemp], ci) : 25);
                        if (t < nextFrame || !imu.Calibrated) continue;
                        nextFrame = t + 1.0 / 60;
                        Quat raw = imu.GetPose(0.015), shown = stab.Update(raw, 1.0 / 60);
                        if (have && still(t)) { double a = Stabilizer.Angle(lastShown, shown) * 180 / Math.PI; sumSq += a * a; n++; if (a > peak) peak = a; }
                        if (turning(t)) maxLag = Math.Max(maxLag, Stabilizer.Angle(raw, shown) * 180 / Math.PI);
                        lastShown = shown; have = true;
                    }
                    Console.WriteLine(string.Format(ci, "{0,-7} still: view moves {1:F4} deg/frame RMS (peak {2:F3})   turning: max lag {3:F2} deg",
                        lvl, Math.Sqrt(sumSq / Math.Max(1, n)), peak, maxLag));
                }
                return 0;
            }
            int rp = Array.IndexOf(args, "--replay");
            if (rp >= 0 && rp + 1 < args.Length)
            {
                // Runs a recorded ImuTest CSV through the real tracker and prints the yaw over time.
                AttachConsole(-1);
                var imu = new XrealImu { DeviceName = "replay" };
                if (Array.IndexOf(args, "--saved") >= 0) imu.UseSavedCalibration();
                var lines = System.IO.File.ReadAllLines(args[rp + 1]);
                var hdr = new List<string>(lines[0].Split(','));
                int it = hdr.IndexOf("t_s"), igx = hdr.IndexOf("gx_dps"), iax = hdr.IndexOf("ax_g"), itemp = hdr.IndexOf("tempC");
                double prevT = -1, nextPrint = 0, yaw0 = double.NaN;
                for (int li = 1; li < lines.Length; li++)
                {
                    var c = lines[li].Split(','); var ci = CultureInfo.InvariantCulture;
                    double t = double.Parse(c[it], ci), dt = prevT < 0 ? 0.001 : Math.Max(0.0001, Math.Min(0.05, t - prevT)); prevT = t;
                    imu.Feed(double.Parse(c[igx], ci), double.Parse(c[igx + 1], ci), double.Parse(c[igx + 2], ci),
                             double.Parse(c[iax], ci), double.Parse(c[iax + 1], ci), double.Parse(c[iax + 2], ci), dt,
                             itemp >= 0 ? double.Parse(c[itemp], ci) : 25);
                    if (t >= nextPrint)
                    {
                        nextPrint = Math.Floor(t) + 1;
                        double yaw = imu.GetPose(0).Yaw() * 180 / Math.PI;
                        if (double.IsNaN(yaw0) && imu.Calibrated) yaw0 = yaw;
                        Console.WriteLine(string.Format(ci, "t={0,5:F0}s yaw={1,7:F1} (rel {2,7:F1})  biasZ={3,7:F3} blocks={4} {5}", t, yaw, double.IsNaN(yaw0) ? 0 : yaw - yaw0, imu.BiasZ, imu.BiasBlocks, imu.Status));
                    }
                }
                imu.DeviceName = "replay"; imu.SaveBias(); return 0;
            }
            int wt = Array.IndexOf(args, "--wiggle-test");
            if (wt >= 0 && wt + 1 < args.Length)
            {
                // Replays a recording and measures what moves the picture while the head turns:
                //  1) motion injected by the accelerometer correction (shipped tracker vs. one with Kp = Ki = 0)
                //  2) picture error left by prediction, with the head pose taken at the frame start vs. just before the refresh
                AttachConsole(-1);
                var lines = System.IO.File.ReadAllLines(args[wt + 1]);
                var hdr = new List<string>(lines[0].Split(','));
                int it = hdr.IndexOf("t_s"), igx = hdr.IndexOf("gx_dps"), iax = hdr.IndexOf("ax_g"), itemp = hdr.IndexOf("tempC");
                var ci = CultureInfo.InvariantCulture;
                const double PxPerDeg = 1080 / 24.8, R2D = 180 / Math.PI;
                int[] ahead = { 0, 10, 20, 30, 40 };
                var imu = new XrealImu { DeviceName = "wiggle" };
                if (wt + 2 < args.Length) imu.AccelTrustMaxDps = double.Parse(args[wt + 2], ci);   // try another gate (1e9 = no gate)
                var gyroOnly = new XrealImu { DeviceName = "wiggle", Kp = 0, Ki = 0 };
                var T = new List<double>(); var rate = new List<double>(); var rel = new List<Quat>(); var pred = new List<Quat[]>();
                double prevT = -1;
                for (int li = 1; li < lines.Length; li++)
                {
                    var c = lines[li].Split(',');
                    double t = double.Parse(c[it], ci), dt = prevT < 0 ? 0.001 : Math.Max(0.0001, Math.Min(0.05, t - prevT)); prevT = t;
                    double gx = double.Parse(c[igx], ci), gy = double.Parse(c[igx + 1], ci), gz = double.Parse(c[igx + 2], ci);
                    double ax = double.Parse(c[iax], ci), ay = double.Parse(c[iax + 1], ci), az = double.Parse(c[iax + 2], ci), temp = itemp >= 0 ? double.Parse(c[itemp], ci) : 25;
                    imu.Feed(gx, gy, gz, ax, ay, az, dt, temp); gyroOnly.Feed(gx, gy, gz, ax, ay, az, dt, temp);
                    if (!imu.Calibrated || !gyroOnly.Calibrated) continue;
                    T.Add(t); rate.Add(Math.Sqrt(gx * gx + gy * gy + gz * gz));
                    rel.Add(imu.GetPose(0) * gyroOnly.GetPose(0).Conj());   // world frame: unchanged by the head motion both trackers share
                    var p = new Quat[ahead.Length]; for (int k = 0; k < ahead.Length; k++) p[k] = imu.GetPose(ahead[k] / 1000.0); pred.Add(p);
                }
                int N = T.Count; if (N < 3000) { Console.WriteLine("recording too short / never calibrated"); return 1; }
                // index of the sample 'ms' after sample i (recordings are ~1 kHz but not exactly)
                Func<int, double, int> after = (i, ms) => { int j = i + (int)ms; while (j < N && T[j] - T[i] < ms / 1000) j++; while (j > i && j < N && T[j - 1] - T[i] >= ms / 1000) j--; return j; };
                // skip the first 2 s after calibration (fast levelling is still settling)
                int i0 = after(0, 2000);
                // 1) accelerometer-injected motion over 100 ms windows, split by head speed
                double sT = 0, nT = 0, pT = 0, sS = 0, nS = 0, pS = 0, sM = 0, nM = 0, turnSec = 0;
                for (int i = i0; ; )
                {
                    int j = after(i, 100); if (j >= N) break;
                    double mean = 0; for (int k = i; k < j; k++) mean += rate[k]; mean /= (j - i);
                    double dps = Stabilizer.Angle(rel[i], rel[j]) * R2D / (T[j] - T[i]);
                    if (mean > 20) { sT += dps * dps; nT++; pT = Math.Max(pT, dps); turnSec += T[j] - T[i]; }
                    else if (mean < 3) { sS += dps * dps; nS++; pS = Math.Max(pS, dps); }
                    else { sM += dps * dps; nM++; }
                    i = j;
                }
                Console.WriteLine(string.Format(ci, "{0}: {1:F0} s tracked, {2:F0} s turning (>20 deg/s)", System.IO.Path.GetFileName(args[wt + 1]), T[N - 1] - T[0], turnSec));
                Console.WriteLine(string.Format(ci, "  accelerometer moves the picture: turning {0:F2} deg/s RMS (peak {1:F2}) = {2:F0} px/s | slow (3-20 deg/s) {5:F2} deg/s RMS | still {3:F3} deg/s RMS (peak {4:F2})",
                    Math.Sqrt(sT / Math.Max(1, nT)), pT, Math.Sqrt(sT / Math.Max(1, nT)) * PxPerDeg, Math.Sqrt(sS / Math.Max(1, nS)), pS, Math.Sqrt(sM / Math.Max(1, nM))));
                // 2) picture error at 60 Hz frames when the picture is seen T ms after the frame started: pose taken at
                //    the frame start and predicted T ms ahead (early), or taken Late ms later and predicted the rest (late)
                const int Late = 10;   // what the render loop gains by waiting for the refresh before measuring
                foreach (int target in new[] { 20, 30, 40 })
                    foreach (int wait in new[] { 0, Late })
                    {
                        int kp = Array.IndexOf(ahead, target - wait);
                        double s = 0, n = 0, peak = 0;
                        for (int i = i0; ; )
                        {
                            int j = after(i, wait), k = after(i, target); if (k >= N) break;
                            if (rate[i] > 20) { double a = Stabilizer.Angle(pred[j][kp], pred[k][0]) * R2D * PxPerDeg; s += a * a; n++; peak = Math.Max(peak, a); }
                            i = after(i, 1000.0 / 60);
                        }
                        Console.WriteLine(string.Format(ci, "  seen {0} ms after frame start, pose taken {1,2} ms in: off by {2,5:F1} px RMS (peak {3,5:F1}) while turning", target, wait, Math.Sqrt(s / Math.Max(1, n)), peak));
                    }
                return 0;
            }
            if (Array.IndexOf(args, "--virtual-test") >= 0)
            {
                // Diagnostics: list virtual monitors, attach the first at 1920x1080, then detach it again.
                AttachConsole(-1);
                var outs = VirtualScreens.List();
                foreach (var o in outs) Console.WriteLine(string.Format("  {0} attached={1} {2}", o.DeviceName, o.Attached, o.MonitorId));
                Console.WriteLine("detached: " + VirtualScreens.DetachAll());
                if (outs.Count > 0)
                {
                    Console.WriteLine("attach " + outs[0].DeviceName + ": " + VirtualScreens.Attach(outs[0].DeviceName));
                    System.Threading.Thread.Sleep(2500);
                    foreach (var m in DisplaySetup.ActiveMonitors()) Console.WriteLine(string.Format("  active {0} {1}x{2} at {3},{4}", m.DeviceName, m.Bounds.Width, m.Bounds.Height, m.Bounds.Left, m.Bounds.Top));
                    Console.WriteLine("detach: " + VirtualScreens.Detach(outs[0].DeviceName));
                    System.Threading.Thread.Sleep(1500);
                    Console.WriteLine("active after detach: " + DisplaySetup.ActiveMonitors().Count);
                }
                return 0;
            }

            int up = Array.IndexOf(args, "--uac-prompts");
            if (up >= 0)
            {
                // Elevated step (Settings checkbox): on = UAC prompts on the normal desktop, so they show in the glasses
                string what = up + 1 < args.Length ? args[up + 1] : "";
                try
                {
                    if (what == "on") UacPrompts.Apply(true);
                    else if (what == "off") UacPrompts.Apply(false);
                    else if (what == "restore") UacPrompts.RestoreIfChangedByUs();
                    return UacPrompts.ShownInGlasses == (what == "on") || what == "restore" ? 0 : 3;
                }
                catch { return 4; }
            }
            int vs = Array.IndexOf(args, "--vdd-screens");
            if (vs >= 0)
            {
                // Elevated step (Settings "Allow up to 8 screens"): set the driver's number of virtual screens and restart it
                int n; if (vs + 1 >= args.Length || !int.TryParse(args[vs + 1], out n)) return 5;
                var lines = new List<string>();
                try
                {
                    string err = XrealDesktop.Setup.VddInstall.SetScreens(n, lines.Add);
                    if (err.Length > 0) lines.Add(err);
                    try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "AirBubble-driver.txt"), lines); } catch { }
                    return err.Length == 0 ? 0 : 3;
                }
                catch (Exception e) { lines.Add(e.ToString()); try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "AirBubble-driver.txt"), lines); } catch { } return 4; }
            }
            int tt = Array.IndexOf(args, "--type-test");
            if (tt >= 0 && tt + 1 < args.Length)
            {
                // AirBubble.exe --type-test <utf-8 text file> [delay ms]: after the delay (default 3000), types the file's
                // text into the focused window exactly like Bubble's type_text tool does. For testing without the AI.
                int delay = 3000;
                if (tt + 2 < args.Length) int.TryParse(args[tt + 2], out delay);
                string text = File.ReadAllText(args[tt + 1], System.Text.Encoding.UTF8);
                System.Threading.Thread.Sleep(Math.Max(0, delay));
                XrealDesktop.PcViewer.Keys.Type(text);
                return 0;
            }
            int vt = Array.IndexOf(args, "--voice-test");
            if (vt >= 0)
            {
                // AirBubble.exe --voice-test <voiceId>: speaks a test line in that ElevenLabs voice (result in %TEMP%\AirBubble-voice.txt)
                string id = vt + 1 < args.Length ? args[vt + 1] : Settings.Load().BubbleVoice, res;
                try
                {
                    var pcm = ElevenLabs.Speak("Hi! I'm Bubble. This is how I sound.", id, System.Threading.CancellationToken.None);
                    AudioOut.Play(pcm, ElevenLabs.Rate);
                    res = "OK " + id + " " + pcm.Length + " samples";
                }
                catch (Exception e) { res = "FAIL " + id + ": " + e.Message; }
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "AirBubble-voice.txt"), res); } catch { }
                return res.StartsWith("OK") ? 0 : 5;
            }
            if (Array.IndexOf(args, "--keys") >= 0)
            {
                // Just the Claude / ElevenLabs key boxes (works while the viewer runs)
                Application.EnableVisualStyles();
                var f = new Form { Text = "Bubble AI keys", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen,
                                   AutoScaleMode = AutoScaleMode.Dpi, Font = new Font("Segoe UI", 9.5f), ClientSize = new Size(520, 150), TopMost = true };
                var boxes = new List<TextBox>();
                string[] names = { "anthropic", "elevenlabs" }, labels = { "Claude API key", "ElevenLabs API key" };
                for (int i = 0; i < 2; i++)
                {
                    f.Controls.Add(new Label { Text = labels[i], AutoSize = true, Location = new Point(14, 20 + i * 40) });
                    var tb = new TextBox { UseSystemPasswordChar = true, Location = new Point(160, 16 + i * 40), Width = 340 };
                    if (Secrets.Get(names[i], "-") != null) tb.Text = "••••••••";
                    f.Controls.Add(tb); boxes.Add(tb);
                }
                var ok = new Button { Text = "Save", Location = new Point(320, 104), Size = new Size(88, 30) };
                var cancel = new Button { Text = "Cancel", Location = new Point(412, 104), Size = new Size(88, 30) };
                ok.Click += delegate { for (int i = 0; i < 2; i++) if (boxes[i].Text != "••••••••") Secrets.Set(names[i], boxes[i].Text); f.Close(); };
                cancel.Click += delegate { f.Close(); };
                f.Controls.Add(ok); f.Controls.Add(cancel); f.AcceptButton = ok; f.CancelButton = cancel;
                Application.Run(f);
                return 0;
            }
            int ba = Array.IndexOf(args, "--bubble-ask");
            if (ba >= 0)
            {
                // Bubble AI without the glasses: one question by text, his answer spoken on this PC and printed
                AttachConsole(-1);
                Directory.CreateDirectory(Settings.Dir);
                Log.Init(Path.Combine(Settings.Dir, "bubble_ask.log"));
                // test switches (not saved): --autonomy 0|1|2, --app-control, --dry-run (decide and log, but don't touch the PC)
                var askSettings = Settings.Load();
                int au = Array.IndexOf(args, "--autonomy"), auLevel;
                if (au >= 0 && au + 1 < args.Length && int.TryParse(args[au + 1], out auLevel)) askSettings.BubbleAutonomy = Math.Max(0, Math.Min(2, auLevel));
                if (Array.IndexOf(args, "--app-control") >= 0) askSettings.BubbleAppControl = true;
                Crew.DryRun = Array.IndexOf(args, "--dry-run") >= 0;
                Log.W("Bubble ask: " + Autonomy.Name(askSettings.BubbleAutonomy) + ", app control " + (askSettings.BubbleAppControl ? "on" : "off") + (Crew.DryRun ? ", dry run" : ""));
                var crew = new Crew(askSettings);
                crew.Toast = s => Log.W("Toast: " + s);
                crew.Main.Inbox.Add(ba + 1 < args.Length ? args[ba + 1] : "Say hi!");
                var sw = Stopwatch.StartNew(); Thread.Sleep(1500);
                var lastCard = new Dictionary<string, string>();
                while (sw.Elapsed.TotalSeconds < 240)
                {
                    var all = crew.Snapshot();
                    var helpers = all.FindAll(a => a.IsHelper);
                    foreach (var a in all)
                    {
                        string prev; lastCard.TryGetValue(a.Name, out prev);
                        if (a.Card != null && a.Card != prev) { lastCard[a.Name] = a.Card; Log.W(a.Name + " card: " + a.Card); }
                    }
                    bool busy = crew.Main.Thinking || crew.Speaker.Busy || crew.Main.Inbox.Count > 0 || helpers.Exists(h => h.Thinking || h.Inbox.Count > 0);
                    if (!busy && sw.Elapsed.TotalSeconds > 4) break;
                    Thread.Sleep(200);
                }
                Thread.Sleep(800);
                crew.Dispose();
                return 0;
            }
            int at = Array.IndexOf(args, "--autonomy-test");
            if (at >= 0 && at + 1 < args.Length)
            {
                // The app's own risk ratings and the ask / don't-ask decision per mode, for a list of sample actions
                // (lines "open|click|type|keys <tab> text"); writes a table to the second path. Touches nothing.
                ActionGuard.ForegroundForTest = () => IntPtr.Zero;
                var outLines = new List<string> { "action\tapp rating\twhy\tsemi\tfull, requested\tfull, not requested" };
                int asked = 0;
                foreach (var line in File.ReadAllLines(args[at + 1]))
                {
                    var kv = line.Split(new[] { '\t' }, 2); if (kv.Length != 2) continue;
                    string why; Risk r;
                    string text = kv[1].Replace("\\n", "\n");
                    if (kv[0] == "open") r = ActionGuard.OpenRisk(text, out why);
                    else if (kv[0] == "click") r = ActionGuard.ClickRisk(0, 0, text, out why);
                    else if (kv[0] == "type") r = ActionGuard.TypeRisk(text, out why);
                    else r = ActionGuard.KeysRisk(text, out why);
                    Func<int, bool, string> d = (level, req) => Autonomy.NeedsAsk(level, r, req) ? "ASK" : "go";
                    if (Autonomy.NeedsAsk(Autonomy.Semi, r, true)) asked++;
                    outLines.Add(kv[0] + " " + kv[1] + "\t" + r + "\t" + (why ?? "") + "\t" + d(Autonomy.Semi, true) + "\t" + d(Autonomy.Full, true) + "\t" + d(Autonomy.Full, false));
                }
                File.WriteAllLines(at + 2 < args.Length ? args[at + 2] : args[at + 1] + ".out.txt", outLines);
                return asked;
            }
            int bs = Array.IndexOf(args, "--bubble-sheet");
            if (bs >= 0 && bs + 1 < args.Length) { Bubble.SaveSheet(args[bs + 1]); return 0; }

            int dm = Array.IndexOf(args, "--display-mode");
            if (dm >= 0)
            {
                // Diagnostics / recovery: read or set the glasses' display mode (2d | 3d | read).
                AttachConsole(-1);
                string what = dm + 1 < args.Length ? args[dm + 1] : "read", err;
                if (what == "2d" || what == "3d")
                {
                    bool ok = XrealMcu.WriteDisplayMode(what == "3d" ? XrealMcu.MODE_SBS_3840_60 : XrealMcu.MODE_2D_1080_60, out err);
                    Console.WriteLine(ok ? "set " + what : "failed: " + err);
                    return ok ? 0 : 1;
                }
                int mode = XrealMcu.ReadDisplayMode(out err);
                Console.WriteLine(mode >= 0 ? "display mode " + mode : "failed: " + err);
                return mode >= 0 ? 0 : 1;
            }

            Directory.CreateDirectory(Settings.Dir);
            Log.Init(Path.Combine(Settings.Dir, "viewer.log"));
            Log.W("AirBubble starting" + (selfTest > 0 ? " (self-test " + selfTest + " s)" : ""));

            bool autostart = Array.IndexOf(args, "--autostart") >= 0;
            bool created;
            using (var mutex = new Mutex(true, "XrealPcViewer.SingleInstance", out created))
            {
                if (!created) { Log.W("Already running."); return 1; }
                // Left over from a crash or an unplug: without the glasses the PC screen must be the primary.
                if (!DisplaySetup.GlassesAttached())
                    try { DisplayConfig.RestorePcDesktop(null, Log.W); } catch (Exception ex) { Log.W("Desktop restore failed: " + ex.Message); }
                Application.EnableVisualStyles();
                var settings = Settings.Load();
                Log.W(string.Format(CultureInfo.InvariantCulture, "Settings: stabilization {0}, predict {1} ms, pc screens {2}, auto drift {3}, bubble {4}, bubble ai {5}",
                    settings.Stabilization, settings.PredictMs, settings.ShowPcScreens, settings.AutoDrift, settings.ShowBubble, settings.BubbleAi));
                if (Array.IndexOf(args, "--3d") >= 0) settings.Sbs3D = true;        // test overrides (not saved)
                if (Array.IndexOf(args, "--2d") >= 0) settings.Sbs3D = false;
                if (Array.IndexOf(args, "--curved") >= 0) settings.Curved = true;
                FixedHead = Array.IndexOf(args, "--fixed-head") >= 0;
                VideoLoad = Array.IndexOf(args, "--video-load") >= 0;
                CalTest = Array.IndexOf(args, "--cal-test") >= 0;
                BubbleTest = Array.IndexOf(args, "--bubble-test") >= 0;
                int ai = Array.IndexOf(args, "--ai-test");
                if (ai >= 0) { AiTest = true; AiTestQuestion = ai + 1 < args.Length ? args[ai + 1] : "Say hi!"; }

                uint restoreTopology = 0; bool set3d = false;
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log.W("Crash: " + e.ExceptionObject); Cleanup(ref set3d, ref restoreTopology); };

                while (true)
                {
                    if (autostart && !WaitForGlasses()) return 0;
                    SetShowing(true);
                    var result = RunOnce(settings, selfTest, autostart, ref restoreTopology, ref set3d);
                    if (result == RunResult.Restart && PendingScreens > 0)
                    {
                        // Settings asked for a different number of virtual screens: the driver restarts, so hand the
                        // desktop back first, change it (elevated), keep the new monitors off, then come back up.
                        int n = PendingScreens; PendingScreens = 0;
                        Cleanup(ref set3d, ref restoreTopology);
                        SetShowing(false);
                        ChangeVirtualScreens(n);
                    }
                    if (result == RunResult.Restart) { Log.W("Restarting viewer"); continue; }
                    Cleanup(ref set3d, ref restoreTopology);
                    SetShowing(false);
                    if (result == RunResult.Unplugged && autostart) continue;
                    Log.W("Exited.");
                    return result == RunResult.Error ? 2 : 0;
                }
            }
        }

        enum RunResult { Quit, Restart, Unplugged, Error }

        /// <summary>Default and maximum size of the virtual screens pool (Settings: "Allow up to 8 screens").</summary>
        public const int DefaultScreens = 4, MaxScreens = 8;
        /// <summary>Set by Settings before a restart: change the driver to this many virtual screens (0 = no change).</summary>
        public static volatile int PendingScreens;

        /// <summary>Virtual screens in the driver config, or -1 if the driver wasn't installed by setup.</summary>
        public static int ConfiguredScreens() { return XrealDesktop.Setup.VddInstall.ConfiguredCount(); }

        /// <summary>Runs this exe elevated with --vdd-screens n (one admin prompt), then keeps the restarted driver's monitors off.</summary>
        static void ChangeVirtualScreens(int n)
        {
            Log.W("Changing virtual screens: " + ConfiguredScreens() + " -> " + n);
            try
            {
                var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--vdd-screens " + n) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                if (!p.WaitForExit(60000)) { Log.W("Virtual screens change: no answer within 60 s"); return; }
                Log.W(p.ExitCode == 0 ? "Virtual screens now " + ConfiguredScreens() : "Virtual screens change failed (code " + p.ExitCode + ", see %TEMP%\\AirBubble-driver.txt)");
            }
            catch (System.ComponentModel.Win32Exception e) { Log.W(e.NativeErrorCode == 1223 ? "Virtual screens change: permission declined" : "Virtual screens change: " + e.Message); return; }
            Thread.Sleep(3000);   // the restarted driver brings its monitors back attached
            try { VirtualScreens.DetachAll(); } catch (Exception e) { Log.W("Detach after driver restart: " + e.Message); }
            Thread.Sleep(1000);
        }

        /// <summary>
        /// Held while the viewer owns the displays (from the moment the glasses are found until the desktop is
        /// restored). The display guard (--guard) stands aside while it exists; a viewer that is only waiting
        /// in the tray for the glasses doesn't hold it, so the guard keeps looking after the desktop then.
        /// </summary>
        public const string ShowingMutexName = "XrealPcViewer.Showing";
        static Mutex s_Showing;
        static void SetShowing(bool on)
        {
            if (on && s_Showing == null) s_Showing = new Mutex(true, ShowingMutexName);
            else if (!on && s_Showing != null) { try { s_Showing.ReleaseMutex(); } catch { } s_Showing.Dispose(); s_Showing = null; }
        }

        static RunResult RunOnce(Settings settings, double selfTest, bool autostart, ref uint restoreTopology, ref bool set3d)
        {
            bool quiet = autostart || selfTest > 0;

            // 1) Glasses display mode: 2D (1920x1080) or side-by-side 3D (3840x1080).
            string err;
            int mode = XrealMcu.ReadDisplayMode(out err);
            Log.W("Glasses display mode: " + (mode >= 0 ? mode.ToString() : "unknown (" + err + ")"));
            byte want = settings.Sbs3D ? XrealMcu.MODE_SBS_3840_60 : XrealMcu.Mode2D(settings.RefreshHz);
            if (mode >= 0 && mode != want)
            {
                if (XrealMcu.WriteDisplayMode(want, out err))
                {
                    set3d = want == XrealMcu.MODE_SBS_3840_60;
                    Log.W("Switched glasses to " + (set3d ? "3D" : "2D") + " (mode " + want + ")");
                    // The display drops and comes back at the new resolution; wait for Windows to see it again.
                    var sw = Stopwatch.StartNew();
                    Thread.Sleep(1500);
                    while (sw.ElapsedMilliseconds < 15000 && !DisplaySetup.GlassesAttached()) Thread.Sleep(250);
                    Log.W(string.Format("Glasses display back after {0:F1} s", sw.Elapsed.TotalSeconds));
                    Thread.Sleep(1000);
                }
                else Log.W("Could not switch display mode: " + err);
            }
            else if (mode == XrealMcu.MODE_SBS_3840_60) set3d = true;

            // 2) Glasses as their own (extended) display. Right after plugging in, a 2D/3D switch or the previous
            //    run's cleanup, the glasses are on USB but Windows hasn't shown their display yet: wait for it.
            if (!DisplaySetup.GlassesAttached() && XrealDevice.Find() != null)
            {
                Log.W("Glasses on USB, waiting for their display...");
                var wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 15000 && !DisplaySetup.GlassesAttached()) Thread.Sleep(250);
                Log.W(DisplaySetup.GlassesAttached() ? string.Format("Glasses display appeared after {0:F1} s", wait.Elapsed.TotalSeconds) : "Glasses display did not appear");
                Thread.Sleep(1000);
            }
            uint prev;
            var glasses = DisplaySetup.EnsureExtended(out prev, Log.W);
            if (prev != 0) restoreTopology = prev;
            if (glasses == null)
            {
                Fail("Couldn't find the glasses as a display.\n\nPlug them into the PC, then press Win+P and choose Extend.", quiet);
                return RunResult.Error;
            }
            for (int i = 0; i < 25 && set3d && glasses.Bounds.Width < glasses.Bounds.Height * 3; i++)
            {
                Thread.Sleep(200);
                glasses = DisplaySetup.ActiveMonitors().Find(m => m.IsGlasses) ?? glasses;
            }
            var mons = DisplaySetup.ActiveMonitors();
            glasses = mons.Find(m => m.IsGlasses) ?? glasses;
            var sources = mons.FindAll(m => !m.IsGlasses);
            foreach (var m in mons)
                Log.W(string.Format("Display {0} {1} at {2},{3} {4}x{5}{6}{7}", m.DeviceName, m.MonitorId, m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height, m.Primary ? " primary" : "", m.IsGlasses ? " GLASSES" : ""));
            if (sources.Count == 0) { Fail("No monitors to show besides the glasses.", quiet); return RunResult.Error; }
            if (glasses.Primary) Log.W("WARNING: the glasses are the primary display; taskbar and new windows will open there.");

            if (!settings.Sbs3D) Log.W("Glasses refresh rate: " + VirtualScreens.SetRefresh(glasses.DeviceName, settings.RefreshHz) + " Hz (wanted " + settings.RefreshHz + ")");

            // 3) Head tracking + viewer.
            var imu = new XrealImu();
            try
            {
                if (!imu.Start()) { Fail(imu.Status, quiet); return RunResult.Error; }
                Log.W("IMU: " + imu.DeviceName + ", " + imu.Status);
                var form = new ViewerForm(glasses, sources, imu, settings, selfTest);
                Application.Run(form);
                if (form.RestartRequested) return RunResult.Restart;
                if (form.GlassesUnplugged) return RunResult.Unplugged;
                return RunResult.Quit;
            }
            finally { imu.Dispose(); }
        }

        /// <summary>Puts the glasses back to 2D and Windows back to its previous display mode.</summary>
        static void Cleanup(ref bool set3d, ref uint restoreTopology)
        {
            string err;
            // (also from 90/120 Hz: Windows can't duplicate the PC screen onto the glasses while they are in a faster mode)
            if (set3d || XrealMcu.ReadDisplayMode(out err) > XrealMcu.MODE_2D_1080_60)
            {
                Log.W("Switching glasses back to 2D 60 Hz");
                XrealMcu.WriteDisplayMode(XrealMcu.MODE_2D_1080_60, out err);
                set3d = false;
                // the glasses' display drops out and comes back in 2D: wait for it, so the restore below can
                // set up Duplicate for them
                var sw = Stopwatch.StartNew();
                Thread.Sleep(1500);
                while (sw.ElapsedMilliseconds < 12000 && XrealDevice.Find() != null && !DisplaySetup.GlassesAttached()) Thread.Sleep(250);
                Thread.Sleep(1000);
            }
            // (Windows' own topology switch isn't used to restore: its Duplicate also duplicates onto the virtual monitors)
            restoreTopology = 0;
            // Virtual screens off, PC screen primary, glasses (if plugged in) duplicating it.
            try { DisplayConfig.RestorePcDesktop(null, Log.W); } catch (Exception ex) { Log.W("Desktop restore failed: " + ex.Message); }
        }

        /// <summary>
        /// Start-with-Windows mode: wait until the glasses are plugged in, with a tray icon so the user can see
        /// it's there and quit. False if asked to quit.
        /// </summary>
        static bool WaitForGlasses()
        {
            var dev0 = XrealDevice.Find();
            if (dev0 != null && dev0.ImuPath != null && DisplaySetup.GlassesAttached()) return true;
            Log.W("Waiting for the glasses to be plugged in...");
            bool quitRequested = false;
            Icon appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName))
            using (var tray = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, Text = "AirBubble: waiting for glasses", Visible = true })
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add("Waiting for XREAL glasses...").Enabled = false;
                menu.Items.Add("Quit", null, delegate { quitRequested = true; });
                tray.ContextMenuStrip = menu;
                var poll = Stopwatch.StartNew();
                while (true)
                {
                    Application.DoEvents();
                    if (quitRequested || quit.WaitOne(100)) { tray.Visible = false; return false; }
                    if (poll.ElapsedMilliseconds < 2000) continue;
                    poll.Restart();
                    var dev = XrealDevice.Find();
                    if (dev != null && dev.ImuPath != null && DisplaySetup.GlassesAttached())
                    {
                        Log.W("Glasses plugged in.");
                        SetShowing(true);   // from now on the viewer sets up the displays, not the guard
                        tray.Visible = false;
                        Thread.Sleep(1500);
                        return true;
                    }
                }
            }
        }

        static void Fail(string msg, bool quiet)
        {
            Log.W("ERROR: " + msg);
            if (!quiet) MessageBox.Show(msg, "AirBubble", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
