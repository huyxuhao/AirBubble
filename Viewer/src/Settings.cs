using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace XrealDesktop.PcViewer
{
    /// <summary>Persisted user settings (%APPDATA%\AirBubble\settings.ini). Read by the render thread live.</summary>
    public sealed class Settings
    {
        public volatile float FovV = 23.5f;        // glasses vertical FOV (deg). Air: 46 deg diagonal -> ~23.5 vertical
        public volatile float Distance = 1.6f;     // m, screen distance
        public volatile float PanelWidth = 1.0f;   // m, main screen width
        public volatile float PredictMs = 15f;     // head pose prediction
        public volatile int RefreshHz = 120;       // glasses refresh rate in 2D: 60, 90 or 120 (ini only). Faster = less smear while the head turns
        public volatile float CaptureFps = 30f;    // window capture rate (monitors follow the desktop's own rate)
        public volatile bool Curved = false;       // monitors on a cylinder around you
        public volatile bool Sbs3D = false;        // side-by-side stereo (glasses switched to 3840x1080)
        public volatile float IpdMm = 63f;         // eye distance for 3D
        public volatile float StereoConverge = 0f; // 3D: 1 / distance (1/m) at which both eyes' images line up exactly; 0 = infinity
        public volatile float StereoVOffset = 0f;  // 3D: right eye image up (+) / down (-) vs the left eye, in pixels
        public volatile bool StereoEdgeMatch = true; // 3D: fade out the strip at the left/right edge that only one eye sees
        public volatile bool LookToInteract = true;// (unused since v1.1)
        public volatile bool ShowPcScreens = false; // also float the PC's own monitor(s) in AR
        public volatile bool AutoDrift = true;      // slowly keep the main screen centred under your gaze
        public volatile int Stabilization = 2;     // head jitter buffer: 0 off, 1 light, 2 medium, 3 strong (walking)
        public volatile bool ShowBubble = false;   // Bubble floats around in AR (click to pop). Local change: off unless turned on in Settings
        public volatile bool BubbleAi = false;     // Bubble listens for his name and talks (Claude + ElevenLabs). Local change: off unless turned on in Settings
        public volatile bool BubbleAppControl = false; // Bubble may click/type/press keys (how often he asks: BubbleAutonomy)
        public volatile int BubbleAutonomy = 0;    // 0 ask every time, 1 semi-autonomous (asks for large/dangerous), 2 fully autonomous (see Autonomy)
        public volatile bool BubbleSpatialAudio = true; // bubbles' voices and pops come from where they float (stereo)
        public volatile bool BubbleFreeFloat = false;   // true: bubbles roam freely and the cursor passes through; false: they hover near one spot and the cursor nudges them
        public volatile string BubbleVoice = DefaultVoice;  // ElevenLabs voice id for the main Bubble ("" = pick one)
        public const string DefaultVoice = "uWNz6TbvrJHW6G4bBMPw";
        /// <summary>ElevenLabs voice id per helper, in Bubble.HelperColors order ("" = default: picked from the account's voices).</summary>
        public readonly string[] HelperVoice = new string[Bubble.HelperColors.Length];
        public string HelperVoiceFor(int index) { var v = index >= 0 && index < HelperVoice.Length ? HelperVoice[index] : null; return string.IsNullOrEmpty(v) ? null : v; }
        public volatile bool ConfineMouse = true;  // AR mouse mode: the Windows pointer stays on the screens shown in AR (never the PC's own screen)
        public volatile float MouseSpeed = 1.0f;   // AR cursor speed multiplier (1 = one screen pixel per mouse count)

        public event Action Changed;
        public void NotifyChanged() { Save(); var c = Changed; if (c != null) c(); }

        static string s_Dir;
        /// <summary>%APPDATA%\AirBubble. On first use, settings from the pre-1.7 folder %APPDATA%\XrealPcViewer are moved over.</summary>
        public static string Dir { get { return s_Dir ?? (s_Dir = MigrateDir()); } }

        static string MigrateDir()
        {
            string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(app, "AirBubble"), old = Path.Combine(app, "XrealPcViewer");
            try
            {
                if (Directory.Exists(dir) || !Directory.Exists(old)) return dir;
                try { Directory.Move(old, dir); return dir; }
                catch { }   // a file is still open (e.g. an old guard's log): copy instead
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(old))
                    try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), false); } catch { }
            }
            catch { }
            return dir;
        }
        static string PathFor() { return Path.Combine(Dir, "settings.ini"); }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(PathFor()))
                {
                    var kv = line.Split('='); if (kv.Length != 2) continue;
                    string k = kv[0].Trim(), v = kv[1].Trim();
                    float f; bool fOk = float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                    bool b = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    switch (k)
                    {
                        case "fovV": if (fOk) s.FovV = Clamp(f, 10, 60); break;
                        case "distance": if (fOk) s.Distance = Clamp(f, 0.5f, 6); break;
                        case "panelWidth": if (fOk) s.PanelWidth = Clamp(f, 0.3f, 4); break;
                        case "predictMs": if (fOk) s.PredictMs = Clamp(f, 0, 50); break;
                        case "refreshHz": if (fOk) s.RefreshHz = f >= 120 ? 120 : f >= 90 ? 90 : 60; break;
                        case "captureFps": if (fOk) s.CaptureFps = Clamp(f, 5, 60); break;
                        case "ipdMm": if (fOk) s.IpdMm = Clamp(f, 50, 80); break;
                        case "stereoConverge": if (fOk) s.StereoConverge = Clamp(f, 0, 2); break;
                        case "stereoVOffset": if (fOk) s.StereoVOffset = Clamp(f, -60, 60); break;
                        case "stereoEdgeMatch": s.StereoEdgeMatch = b; break;
                        case "curved": s.Curved = b; break;
                        case "sbs3d": s.Sbs3D = b; break;
                        case "lookToInteract": s.LookToInteract = b; break;
                        case "mouseSpeed": if (fOk) s.MouseSpeed = Clamp(f, 0.3f, 3f); break;
                        case "showPcScreens": s.ShowPcScreens = b; break;
                        case "autoDrift": s.AutoDrift = b; break;
                        case "bubble": s.ShowBubble = b; break;
                        case "bubbleAi": s.BubbleAi = b; break;
                        case "bubbleAppControl": s.BubbleAppControl = b; break;
                        case "bubbleSpatial": s.BubbleSpatialAudio = b; break;
                        case "bubbleFreeFloat": s.BubbleFreeFloat = b; break;
                        case "confineMouse": s.ConfineMouse = b; break;
                        case "bubbleAutonomy": if (fOk) s.BubbleAutonomy = (int)Clamp(f, 0, 2); break;
                        case "bubbleVoice": s.BubbleVoice = v.Length > 0 ? v : DefaultVoice; break;
                        case "stabilization": if (fOk) s.Stabilization = (int)Clamp(f, 0, 3); break;
                        default:
                            if (k.StartsWith("voice", StringComparison.Ordinal))
                            {
                                int i = Array.FindIndex(Bubble.HelperColors, n => "voice" + n == k);
                                if (i >= 0) s.HelperVoice[i] = v;
                            }
                            break;
                    }
                }
            }
            catch (Exception e) { Log.W("Settings: defaults used, could not read " + PathFor() + ": " + e.Message); }
            return s;
        }

        static float Clamp(float v, float lo, float hi) { return Math.Max(lo, Math.Min(hi, v)); }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var helpers = new System.Text.StringBuilder();
                for (int i = 0; i < HelperVoice.Length; i++) helpers.Append("voice" + Bubble.HelperColors[i] + "=" + (HelperVoice[i] ?? "") + "\r\n");
                File.WriteAllText(PathFor(), helpers.Insert(0, string.Format(CultureInfo.InvariantCulture,
                    "fovV={0}\r\ndistance={1}\r\npanelWidth={2}\r\npredictMs={3}\r\ncaptureFps={4}\r\nipdMm={5}\r\ncurved={6}\r\nsbs3d={7}\r\nlookToInteract={8}\r\nmouseSpeed={9}\r\nshowPcScreens={10}\r\nautoDrift={11}\r\nstabilization={12}\r\nstereoConverge={13}\r\nstereoVOffset={14}\r\nstereoEdgeMatch={15}\r\nbubble={16}\r\nbubbleAi={17}\r\nbubbleAppControl={18}\r\nbubbleVoice={19}\r\nbubbleSpatial={20}\r\nbubbleAutonomy={21}\r\nconfineMouse={22}\r\nbubbleFreeFloat={23}\r\nrefreshHz={24}\r\n",
                    FovV, Distance, PanelWidth, PredictMs, CaptureFps, IpdMm, Curved ? 1 : 0, Sbs3D ? 1 : 0, LookToInteract ? 1 : 0, MouseSpeed, ShowPcScreens ? 1 : 0, AutoDrift ? 1 : 0, Stabilization,
                    StereoConverge, StereoVOffset, StereoEdgeMatch ? 1 : 0, ShowBubble ? 1 : 0, BubbleAi ? 1 : 0, BubbleAppControl ? 1 : 0, BubbleVoice ?? "", BubbleSpatialAudio ? 1 : 0, BubbleAutonomy, ConfineMouse ? 1 : 0, BubbleFreeFloat ? 1 : 0, RefreshHz)).ToString());
            }
            catch { }
        }

        // ---- Start with Windows: shortcut in the Startup folder (see StartupLinks) ----
        public static bool StartWithWindows
        {
            get { return StartupLinks.ViewerEnabled || StartupLinks.LegacyViewerAutostart(); }
            set { StartupLinks.SetViewer(value); }
        }
    }
}
