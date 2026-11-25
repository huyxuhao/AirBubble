using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using XrealDesktop.Host;

namespace XrealDesktop.PcViewer
{
    /// <summary>
    /// The AR desktop: every PC screen (the real monitor plus virtual screens from the bundled driver) floats
    /// around you as a window you can move, push/pull, resize, minimize, maximize, lock and close, using an AR
    /// mouse cursor. Over a screen's content the mouse works normally (it drives the real Windows cursor).
    /// </summary>
    sealed partial class ViewerForm : Form
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT r);
        [DllImport("user32.dll", EntryPoint = "ClipCursor")] static extern bool ClipCursorNone(IntPtr r);
        [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("opengl32.dll", EntryPoint = "glReadPixels")] static extern void ReadPixels(int x, int y, int w, int h, uint fmt, uint type, IntPtr data);
        [DllImport("opengl32.dll", EntryPoint = "glReadBuffer")] static extern void ReadBuffer(uint mode);

        const int WM_HOTKEY = 0x0312;
        enum Hk { Recenter = 1, Closer, Farther, FovDown, FovUp, Quit, Recal, Settings, Arrange, Mouse, NewScreen, Calibrate3D, Talk, Yes, No,
                  // keys grabbed only while the 3D calibration is open
                  CalLeft = 100, CalRight, CalUp, CalDown, CalIpdDown, CalIpdUp, CalEdges, CalReset, CalSnap, CalSave, CalCancel }
        static readonly Hk[] s_CalKeys = { Hk.CalLeft, Hk.CalRight, Hk.CalUp, Hk.CalDown, Hk.CalIpdDown, Hk.CalIpdUp, Hk.CalEdges, Hk.CalReset, Hk.CalSnap, Hk.CalSave, Hk.CalCancel };
        static readonly uint[] s_CalVk = { 0x25, 0x27, 0x26, 0x28, 0xDB, 0xDD, 0x45, 0x52, 0x20, 0x0D, 0x1B };   // arrows, [ ] E R Space Enter Esc
        const double Deg = Math.PI / 180;

        readonly MonitorEntry m_Glasses;
        readonly XrealImu m_Imu;
        readonly Settings m_Settings;
        readonly double m_SelfTestSeconds;
        readonly bool m_Stereo;
        readonly Stopwatch m_Clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer m_UiTimer = new System.Windows.Forms.Timer();
        readonly ManualResetEvent m_Closed = new ManualResetEvent(false);
        NotifyIcon m_Tray;
        SettingsForm m_SettingsForm;
        InputHook m_Input;
        Thread m_RenderThread;
        volatile bool m_Running = true;
        public bool RestartRequested, GlassesUnplugged;
        public double RenderFps;

        // screens (copy-on-write list; render thread reads snapshots)
        readonly object m_Lock = new object();
        List<ScreenWin> m_Screens = new List<ScreenWin>();
        readonly List<ScreenWin> m_Dead = new List<ScreenWin>();
        Dictionary<string, LayoutStore.Entry> m_Layout;
        volatile bool m_Busy;              // a screen add/remove is in progress
        string m_OriginalPrimary;          // the PC's primary display, restored on exit
        string m_MainDevice;               // the virtual screen that is the Windows primary while we run
        int m_PrimaryFixFails;             // consecutive failed attempts to put the primary back (stop after 3)
        long m_LastPrimaryCheck;

        // head / view (render thread)
        Quat m_Anchor = Quat.Identity, m_Rel = Quat.Identity;
        volatile bool m_RecenterRequested, m_ReadbackRequested, m_ArrangeRequested;
        volatile string m_ReadbackName = "selftest_render.png";
        double m_HFov, m_VFov;

        // AR cursor + interaction (render thread)
        double m_CurYaw, m_CurPitch = -2;
        enum Mode { Idle, ContentPress, Move, Resize, Press }
        Mode m_Mode = Mode.Idle;
        ScreenWin m_Target; ScreenWin.Part m_TargetPart; string m_PressId;
        double m_OffYaw, m_OffPitch;
        int m_ContentButtons;
        DateTime m_LastTitleClick = DateTime.MinValue; ScreenWin m_LastTitleScreen;
        Hit m_Hover;
        POINT m_LastSetCursor;

        struct Hit
        {
            public ScreenWin S; public ScreenWin.Part Part; public double U, V, Lx, Ly, T;
            public string Dock;   // dock item id when over the dock
            public bool OnDock;
        }

        // dock
        uint m_DockTex; int m_DockW, m_DockH; string m_DockStamp; List<RectangleF> m_DockRects = new List<RectangleF>();
        List<Chrome.DockItem> m_DockItems = new List<Chrome.DockItem>();
        const double DockPitch = -27, DockDist = 0.9, DockHeight = 0.034;

        // cursor sprites
        uint m_ArrowTex; readonly Dictionary<IntPtr, uint> m_CursorTex = new Dictionary<IntPtr, uint>();
        readonly Dictionary<IntPtr, Point> m_CursorHot = new Dictionary<IntPtr, Point>();

        // frame timing (render thread): for judder diagnostics
        int m_LateFrames; double m_WorstFrameMs, m_UploadMs, m_UploadCount, m_LastSwapMs, m_DrawMs, m_SwapMs;
        readonly int[] m_Cadence = new int[5];     // how many refreshes each video frame of the main screen stayed up
        int m_SinceNewFrame;
        static readonly bool s_NoPacing = Environment.GetEnvironmentVariable("XR_NO_PACING") != null;
        Stopwatch m_FrameClock = Stopwatch.StartNew();

        // head jitter buffer (render thread)
        readonly Stabilizer m_Stab = new Stabilizer();

        // drift absorption (render thread)
        double m_DriftAcc, m_DriftW, m_DriftT, m_DriftPending, m_DriftTotal, m_LastHeadYaw;

        // HUD toast
        string m_Toast; DateTime m_ToastUntil;

        // Bubble, the little friend floating around (render thread)
        readonly Bubble m_Bubble = new Bubble();
        volatile bool m_BubbleTestPlace;

        public ViewerForm(MonitorEntry glasses, List<MonitorEntry> sources, XrealImu imu, Settings settings, double selfTestSeconds)
        {
            m_Glasses = glasses; m_Imu = imu; m_Settings = settings; m_SelfTestSeconds = selfTestSeconds;
            m_Stereo = glasses.Bounds.Width >= glasses.Bounds.Height * 3;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; TopMost = true; BackColor = Color.Black; Text = "AirBubble";
            var b = glasses.Bounds; Bounds = new Rectangle(b.Left, b.Top, b.Width, b.Height);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
            m_Imu.CalibrationDone += () => { m_RecenterRequested = true; };
            m_Layout = LayoutStore.Load();
            bool lastShowPc = settings.ShowPcScreens;
            settings.Changed += () => { if (settings.ShowPcScreens != lastShowPc) { lastShowPc = settings.ShowPcScreens; Ui(RefreshScreens); } };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x8; return cp; }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        // =====================================================================================================
        // lifecycle
        // =====================================================================================================
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var b = m_Glasses.Bounds;
            SetWindowPos(Handle, (IntPtr)(-1), b.Left, b.Top, b.Width, b.Height, 0x0010);

            uint mods = 0x0002 | 0x0001 | 0x4000;
            RegisterHotKey(Handle, (int)Hk.Recenter, mods, 0x52);   // R
            RegisterHotKey(Handle, (int)Hk.Closer, mods, 0xBB);     // =
            RegisterHotKey(Handle, (int)Hk.Farther, mods, 0xBD);    // -
            RegisterHotKey(Handle, (int)Hk.FovDown, mods, 0xDB);    // [
            RegisterHotKey(Handle, (int)Hk.FovUp, mods, 0xDD);      // ]
            RegisterHotKey(Handle, (int)Hk.Quit, mods, 0x51);       // Q
            RegisterHotKey(Handle, (int)Hk.Recal, mods, 0x43);      // C
            RegisterHotKey(Handle, (int)Hk.Settings, mods, 0x53);   // S
            RegisterHotKey(Handle, (int)Hk.Arrange, mods, 0x41);    // A
            RegisterHotKey(Handle, (int)Hk.Mouse, mods, 0x47);      // G
            RegisterHotKey(Handle, (int)Hk.NewScreen, mods, 0x4E);  // N
            RegisterHotKey(Handle, (int)Hk.Calibrate3D, mods, 0x4B); // K
            RegisterHotKey(Handle, (int)Hk.Talk, mods, 0x42);       // B: talk to Bubble

            m_Input = new InputHook(Handle);
            SetArMouse(true);

            Icon appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            m_Tray = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, Text = "AirBubble", Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Settings...  (Ctrl+Alt+S)", null, delegate { ShowSettings(); });
            menu.Items.Add("New screen  (Ctrl+Alt+N)", null, delegate { AddScreen(); });
            menu.Items.Add("Arrange screens  (Ctrl+Alt+A)", null, delegate { m_ArrangeRequested = true; });
            menu.Items.Add("Recenter  (Ctrl+Alt+R)", null, delegate { m_RecenterRequested = true; });
            menu.Items.Add("Mouse: AR / PC  (Ctrl+Alt+G)", null, delegate { SetArMouse(!m_Input.Active); });
            menu.Items.Add("Recalibrate  (Ctrl+Alt+C)", null, delegate { m_Imu.Recalibrate(); Toast("Hold still: calibrating"); });
            menu.Items.Add("Calibrate 3D  (Ctrl+Alt+K)", null, delegate { StartCalibration3D(); });
            var bubbleItem = new ToolStripMenuItem("Bubble") { Checked = m_Settings.ShowBubble, CheckOnClick = true };
            bubbleItem.CheckedChanged += delegate { m_Settings.ShowBubble = bubbleItem.Checked; m_Settings.NotifyChanged(); };
            menu.Opening += delegate { bubbleItem.Checked = m_Settings.ShowBubble; };
            menu.Items.Add(bubbleItem);
            menu.Items.Add("Talk to Bubble  (Ctrl+Alt+B)", null, delegate { if (m_Crew != null) m_Crew.PushToTalk(); else Toast("Bubble's AI is off (Settings > Bubble AI)"); });
            var autoMenu = new ToolStripMenuItem("Bubble works");
            for (int i = 0; i < Autonomy.Names.Length; i++)
            {
                int level = i;
                autoMenu.DropDownItems.Add(new ToolStripMenuItem(Autonomy.Names[i], null, delegate
                {
                    if (m_Settings.BubbleAutonomy == level || !SettingsForm.ConfirmAutonomy(this, level)) return;
                    m_Settings.BubbleAutonomy = level; m_Settings.NotifyChanged();
                    Toast("Bubble: " + Autonomy.Names[level].ToLowerInvariant() + (level != Autonomy.AskAlways && !m_Settings.BubbleAppControl ? " (clicking and typing are still off in Settings)" : ""));
                }));
            }
            menu.Opening += delegate { for (int i = 0; i < autoMenu.DropDownItems.Count; i++) ((ToolStripMenuItem)autoMenu.DropDownItems[i]).Checked = i == m_Settings.BubbleAutonomy; };
            menu.Items.Add(autoMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit  (Ctrl+Alt+Q)", null, delegate { Close(); });
            m_Tray.ContextMenuStrip = menu;

            var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
            new Thread(() => { if (WaitHandle.WaitAny(new WaitHandle[] { quitEvent, m_Closed }) == 0) { try { BeginInvoke((Action)Close); } catch { } } quitEvent.Dispose(); }) { IsBackground = true }.Start();

            m_UiTimer.Interval = 15; m_UiTimer.Tick += delegate { UiTick(); }; m_UiTimer.Start();

            // Screens: virtual screens only (the PC's own screen isn't mirrored). Re-open the ones from last time,
            // or one new screen, make the first one the Windows primary while we run, and bring windows over.
            var reopen = new List<string>();
            foreach (var kv in m_Layout) if (kv.Key.StartsWith("virtual:") && kv.Value.Open) reopen.Add(kv.Key.Substring(8));
            var outs = VirtualScreens.List();
            var origPrimary = DisplaySetup.ActiveMonitors().Find(m => m.Primary);
            m_OriginalPrimary = origPrimary != null && !IsVirtual(origPrimary) ? origPrimary.DeviceName : null;
            if (outs.Count == 0)
            {
                Toast("Virtual screens driver not installed: showing the PC screen (run setup to add it)");
                RefreshScreens();
            }
            else
            {
                m_Busy = true;
                Task.Factory.StartNew(() =>
                {
                    // Switching Windows to Extend attaches every display, including all virtual ones at their
                    // default mode. Keep exactly the screens that were open last time (or one), at 1920x1080.
                    var keep = outs.FindAll(o => reopen.Contains(o.DeviceName));
                    if (keep.Count == 0) keep.Add(outs[0]);
                    // One atomic arrangement: [glasses] [main AR screen = primary at 0,0] [PC screen(s)] [other AR screens]
                    // remembered screens that Windows didn't switch on: switch them on first
                    bool turnedOn = false;
                    foreach (var k in keep) if (!k.Attached) { VirtualScreens.Attach(k.DeviceName, VirtualScreens.DefaultWidth, VirtualScreens.DefaultHeight, false); turnedOn = true; }
                    if (turnedOn) { VirtualScreens.Apply(); Thread.Sleep(1200); }
                    var cur = DisplaySetup.ActiveMonitors();
                    var attached = VirtualScreens.List().FindAll(o => o.Attached);
                    var keepDevs = new List<string>();
                    foreach (var k in keep) { var a = attached.Find(o => o.DeviceName == k.DeviceName); if (a != null) keepDevs.Add(a.DeviceName); }
                    if (keepDevs.Count == 0 && attached.Count > 0) keepDevs.Add(attached[0].DeviceName);
                    if (keepDevs.Count == 0)
                    {
                        Log.W("No virtual screen came on: " + (VirtualScreens.LastError ?? "no error reported"));
                        // nothing attached yet (e.g. Windows didn't extend onto them): attach one the classic way first
                        VirtualScreens.Attach(outs[0].DeviceName); Thread.Sleep(1200);
                        var a = VirtualScreens.List().Find(o => o.Attached); if (a != null) keepDevs.Add(a.DeviceName);
                        cur = DisplaySetup.ActiveMonitors();
                    }
                    var want = new List<DisplayConfig.Want>();
                    int vw = VirtualScreens.DefaultWidth, vh = VirtualScreens.DefaultHeight, x = 0;
                    if (keepDevs.Count > 0) { want.Add(new DisplayConfig.Want { Device = keepDevs[0], Width = vw, Height = vh, X = 0, Y = 0 }); x = vw; }
                    // Local change: this laptop sits to the RIGHT of where the AR main screen floats, so the PC screen(s)
                    // go right of the main screen and the glasses go left (upstream: PC left, glasses right).
                    foreach (var m in cur) if (!m.IsGlasses && !IsVirtual(m)) { want.Add(new DisplayConfig.Want { Device = m.DeviceName, X = x, Y = 0 }); x += m.Bounds.Width; }
                    for (int k = 1; k < keepDevs.Count; k++) { want.Add(new DisplayConfig.Want { Device = keepDevs[k], Width = vw, Height = vh, X = x, Y = 0 }); x += vw; }
                    var gl = cur.Find(m => m.IsGlasses);
                    if (gl != null) want.Add(new DisplayConfig.Want { Device = gl.DeviceName, X = -gl.Bounds.Width, Y = 0 });
                    // no virtual screen came on: leave Windows as it is (PC screen primary); never make the glasses primary
                    int rc = keepDevs.Count > 0 ? DisplayConfig.Apply(want, DisplayConfig.SaveArLayout) : -1;
                    Log.W(keepDevs.Count > 0 ? "Desktop arranged for AR (" + want.Count + " displays, main " + keepDevs[0] + "): " + (rc == 0 ? "ok" : "error " + rc) : "Desktop left as it is: no virtual screen to show");
                    Thread.Sleep(1200);
                    if (keepDevs.Count > 0) { m_MainDevice = keepDevs[0]; DisplayConfig.EnsurePrimary(m_MainDevice, Log.W); }
                    foreach (var m in DisplaySetup.ActiveMonitors()) Log.W(string.Format("  {0} {1}x{2} at {3},{4}{5}{6}", m.DeviceName, m.Bounds.Width, m.Bounds.Height, m.Bounds.Left, m.Bounds.Top, m.Primary ? " primary" : "", m.IsGlasses ? " glasses" : ""));
                    // Local change: Windows doesn't always put the displays where asked on the first go (seen with the PC
                    // screen right of the main screen): compare with what was asked and ask once more.
                    var nowMons = DisplaySetup.ActiveMonitors();
                    if (keepDevs.Count > 0 && want.Exists(w => { var a = nowMons.Find(m => m.DeviceName == w.Device); return a != null && (a.Bounds.Left != w.X || a.Bounds.Top != w.Y); }))
                    {
                        Log.W("AR layout wanted: " + string.Join(", ", want.ConvertAll(w => w.Device + " at " + w.X + "," + w.Y)));
                        rc = DisplayConfig.Apply(want, DisplayConfig.SaveArLayout);
                        Log.W("AR layout differed: applied again -> " + (rc == 0 ? "ok" : "error " + rc));
                        Thread.Sleep(1200);
                        foreach (var m in DisplaySetup.ActiveMonitors()) Log.W(string.Format("  {0} {1}x{2} at {3},{4}{5}{6}", m.DeviceName, m.Bounds.Width, m.Bounds.Height, m.Bounds.Left, m.Bounds.Top, m.Primary ? " primary" : "", m.IsGlasses ? " glasses" : ""));
                    }
                    BeginInvoke((Action)(() =>
                    {
                        FollowGlassesDisplay();
                        RefreshScreens(); m_Busy = false;
                        RescueWindows(true);
                        SyncWindowsLayout();
                    }));
                });
            }

            m_RenderThread = new Thread(RenderLoop) { IsBackground = true, Name = "render", Priority = ThreadPriority.AboveNormal };
            m_RenderThread.Start();

            if (m_SelfTestSeconds > 0) StartSelfTest();
            Toast("Hold still: calibrating");
            if (m_Stereo) Log.W("Rendering side-by-side 3D");
            if (m_SelfTestSeconds <= 0 || Program.AiTest) StartCrewIfWanted();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            m_Running = false;
            if (m_RenderThread != null) m_RenderThread.Join(1500);
            m_UiTimer.Stop(); m_Closed.Set();
            SaveLayout();
            try { DisplayConfig.RestorePcDesktop(m_OriginalPrimary, Log.W, false); }   // Duplicate for the glasses comes in Cleanup, after they are back in 2D
            catch (Exception ex) { Log.W("Desktop restore failed: " + ex.Message); }
            if (m_Input != null) m_Input.Dispose();
            StopCrew();
            ClipCursorNone(IntPtr.Zero);
            if (m_SettingsForm != null && !m_SettingsForm.IsDisposed) m_SettingsForm.Close();
            foreach (Hk h in Enum.GetValues(typeof(Hk))) UnregisterHotKey(Handle, (int)h);
            foreach (var s in Snapshot()) if (s.Feed != null) s.Feed.Dispose();
            if (m_Tray != null) { m_Tray.Visible = false; m_Tray.Dispose(); }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_INPUT && m_Input != null) m_Input.OnRawInput(m.LParam);
            // the Windows pointer is never drawn over the AR view itself (only the AR cursor is)
            if (m.Msg == 0x0020 && (m.LParam.ToInt64() & 0xFFFF) == 1) { SetCursor(IntPtr.Zero); m.Result = (IntPtr)1; return; }   // WM_SETCURSOR, HTCLIENT
            if (m.Msg == WM_HOTKEY)
            {
                switch ((Hk)m.WParam.ToInt32())
                {
                    case Hk.Recenter: m_RecenterRequested = true; Toast("Recentered"); break;
                    case Hk.Closer: PushPull(1 / 1.12); break;
                    case Hk.Farther: PushPull(1.12); break;
                    case Hk.FovDown: m_Settings.FovV = Math.Max(10, m_Settings.FovV - 0.5f); m_Settings.Save(); Toast(string.Format("Field of view {0:F1} deg", m_Settings.FovV)); break;
                    case Hk.FovUp: m_Settings.FovV = Math.Min(60, m_Settings.FovV + 0.5f); m_Settings.Save(); Toast(string.Format("Field of view {0:F1} deg", m_Settings.FovV)); break;
                    case Hk.Recal: m_Imu.Recalibrate(); Toast("Hold still: calibrating"); break;
                    case Hk.Settings: ShowSettings(); break;
                    case Hk.Arrange: m_ArrangeRequested = true; break;
                    case Hk.Mouse: SetArMouse(!m_Input.Active); break;
                    case Hk.NewScreen: AddScreen(); break;
                    case Hk.Quit: Close(); break;
                    case Hk.Calibrate3D: if (m_Calibrating3D) EndCalibration3D(true); else StartCalibration3D(); break;
                    case Hk.Talk: if (m_Crew != null) m_Crew.PushToTalk(); else Toast("Bubble's AI is off (Settings > Bubble AI)"); break;
                    case Hk.Yes: if (m_Crew != null) m_Crew.Answer(true); break;
                    case Hk.No: if (m_Crew != null) m_Crew.Answer(false); break;
                    default: CalibrationKey((Hk)m.WParam.ToInt32()); break;
                }
                return;
            }
            base.WndProc(ref m);
        }

        void Toast(string s) { m_Toast = s; m_ToastUntil = DateTime.Now.AddSeconds(2.5); Log.W("UI: " + s); }

        void SetArMouse(bool on)
        {
            m_Input.Active = on;
            ScreenFeed.DrawCursorInFrames = !on;   // in AR mode we draw our own cursor
            if (on) ClipCursorNone(IntPtr.Zero);
            m_DockStamp = null;
            Toast(on ? "Mouse cursor is in the glasses" : "Mouse on the normal desktop (Ctrl+Alt+G to bring it back)");
        }

        /// <summary>UI thread housekeeping: PC-mode cursor confinement, stray windows, unplug detection.</summary>
        void UiTick()
        {
            // (while the AR cursor follows the real pointer, the pointer moves like in PC mode: jump across the glasses' display)
            if (!m_Input.Active || m_Clock.ElapsedMilliseconds - m_LastFollowMs < 500) KeepCursorOffGlasses(); else KeepRealCursorHome();
            if (m_Clock.ElapsedMilliseconds % 1000 < 16 && !m_Busy) RescueWindows(false);
            if (m_Imu.Disconnected && !GlassesUnplugged) { GlassesUnplugged = true; Log.W("Glasses unplugged; closing."); Close(); }
            if (m_Clock.ElapsedMilliseconds - m_LastPrimaryCheck > 2000 && !m_Busy && !GlassesUnplugged) { m_LastPrimaryCheck = m_Clock.ElapsedMilliseconds; KeepMainPrimary(); }
            CrewUiTick();
        }

        /// <summary>
        /// While the viewer runs, the Windows primary (taskbar, Start, new windows) must be a screen you can see in
        /// the glasses. Windows can hand it back to the PC screen on its own (display changes, Win+P, legacy
        /// attach/detach calls), so check every 2 s and put it back.
        /// </summary>
        void KeepMainPrimary()
        {
            if (m_PrimaryFixFails >= 3) return;
            var mons = DisplaySetup.ActiveMonitors();
            var prim = mons.Find(m => m.Primary);
            var shown = Snapshot().FindAll(s => s.Virtual && s.Mon != null);
            if (shown.Count == 0) return;   // no virtual screens (driver missing): the PC screen itself is shown
            if (prim != null && IsVirtual(prim) && shown.Exists(s => s.Mon.DeviceName == prim.DeviceName)) { m_MainDevice = prim.DeviceName; m_PrimaryFixFails = 0; return; }
            string dev = m_MainDevice != null && mons.Exists(m => m.DeviceName == m_MainDevice && IsVirtual(m)) ? m_MainDevice : shown[0].Mon.DeviceName;
            Log.W("Primary display is " + (prim != null ? prim.DeviceName : "none") + ", not in the glasses: moving it to " + dev);
            m_Busy = true;
            Task.Factory.StartNew(() =>
            {
                bool ok = DisplayConfig.EnsurePrimary(dev, Log.W);
                Thread.Sleep(400);
                Ui(() =>
                {
                    m_PrimaryFixFails = ok ? 0 : m_PrimaryFixFails + 1;
                    if (ok) m_MainDevice = dev;
                    FollowGlassesDisplay();
                    RefreshScreens(); m_Busy = false;
                    if (ok) RescueWindows(false);
                    else if (m_PrimaryFixFails >= 3) Toast("Windows won't make a glasses screen the main display");
                });
            });
        }

        void ShowSettings()
        {
            if (m_SettingsForm == null || m_SettingsForm.IsDisposed)
            {
                m_SettingsForm = new SettingsForm(m_Settings, () => { RestartRequested = true; Close(); }, StartCalibration3D, m_Imu.DeviceName);
                m_SettingsForm.Show();
            }
            // The settings window opens on the main screen; use it with the AR cursor like any other window.
            ForceForeground(m_SettingsForm.Handle);
        }

        static void ForceForeground(IntPtr h)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == h) return;
            uint pid; uint fgThread = Native.GetWindowThreadProcessId(fg, out pid), me = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            Native.BringWindowToTop(h); Native.SetForegroundWindow(h);
            if (attached) Native.AttachThreadInput(me, fgThread, false);
        }

        // =====================================================================================================
        // screens
        // =====================================================================================================
        List<ScreenWin> Snapshot() { lock (m_Lock) return m_Screens; }

        /// <summary>Syncs the AR windows with the monitors Windows currently has (minus the glasses).</summary>
        void RefreshScreens()
        {
            var all = DisplaySetup.ActiveMonitors().FindAll(m => !m.IsGlasses);
            bool haveVirtual = all.Exists(IsVirtual);
            var mons = all.FindAll(m => IsVirtual(m) || m_Settings.ShowPcScreens || !haveVirtual);
            var virt = VirtualScreens.List();
            var old = Snapshot();
            var next = new List<ScreenWin>();
            int vIndex = 0;
            foreach (var m in mons)
            {
                bool isVirtual = IsVirtual(m);
                // virtual screens are keyed by their display output (stable while the driver is installed);
                // Windows may renumber the monitor id when a monitor is switched off and on again
                string key = isVirtual ? "virtual:" + m.DeviceName : (m.MonitorId ?? m.DeviceName);
                var s = old.Find(x => x.Key == key);
                if (s == null)
                {
                    s = new ScreenWin { Key = key, Virtual = isVirtual };
                    LayoutStore.Entry le;
                    if (m_Layout.TryGetValue(key, out le)) { s.Yaw = le.Yaw; s.Pitch = le.Pitch; s.Dist = le.Dist; s.Width = le.Width; s.Minimized = le.Minimized; s.Locked = le.Locked; }
                    else PlaceNew(s, next.Count == 0 && old.Count == 0);
                }
                if (s.Feed == null || s.Mon == null || s.Mon.Bounds.Left != m.Bounds.Left || s.Mon.Bounds.Top != m.Bounds.Top || s.Mon.Bounds.Width != m.Bounds.Width || s.Mon.Bounds.Height != m.Bounds.Height || s.Mon.DeviceName != m.DeviceName)
                {
                    if (s.Feed != null) s.Feed.Dispose();
                    s.Mon = m;
                    s.Feed = new ScreenFeed(m, 2560);
                    s.Feed.Start();
                    s.Seen = 0;
                }
                if (isVirtual) { vIndex = virt.FindIndex(o => o.DeviceName == m.DeviceName); s.Title = m.Primary ? "Main screen" : "Screen " + (vIndex + 1); }
                else s.Title = haveVirtual ? "This PC's screen" : "Main screen";
                s.TitleStamp = null;
                next.Add(s);
            }
            lock (m_Lock)
            {
                foreach (var s in old) if (!next.Contains(s)) { if (s.Feed != null) s.Feed.Dispose(); m_Dead.Add(s); }
                m_Screens = next;
            }
            m_DockStamp = null;
            Log.W("Screens: " + string.Join(", ", next.ConvertAll(s => s.Title + " " + s.Mon.Bounds.Width + "x" + s.Mon.Bounds.Height)));
        }

        /// <summary>The glasses' display can move in Windows' coordinates when the desktop is rearranged: keep our window on it.</summary>
        void FollowGlassesDisplay()
        {
            var gl = DisplaySetup.ActiveMonitors().Find(m => m.IsGlasses);
            if (gl == null) return;
            if (gl.Bounds.Left != m_Glasses.Bounds.Left || gl.Bounds.Top != m_Glasses.Bounds.Top)
            {
                m_Glasses.Bounds = gl.Bounds;
                SetWindowPos(Handle, (IntPtr)(-1), gl.Bounds.Left, gl.Bounds.Top, gl.Bounds.Width, gl.Bounds.Height, 0x0010);
                Log.W("Glasses display now at " + gl.Bounds.Left + "," + gl.Bounds.Top);
            }
        }

        static bool IsVirtual(MonitorEntry m) { return DisplayConfig.IsVirtual(m); }

        /// <summary>The main AR screen: the Windows primary among the shown screens (normally a virtual one).</summary>
        ScreenWin MainScreen()
        {
            var list = Snapshot();
            return list.Find(s => s.Mon != null && s.Mon.Primary) ?? list.Find(s => s.Virtual && s.Mon != null) ?? list.Find(s => s.Mon != null);
        }

        /// <summary>Default placement for a screen without a saved layout: where you're looking, nudged off others.</summary>
        void PlaceNew(ScreenWin s, bool first)
        {
            s.Dist = m_Settings.Distance; s.Width = m_Settings.PanelWidth;
            double hy = 0, hp = 0;
            if (!first) { double fx, fy, fz; m_Rel.Rotate(0, 0, -1, out fx, out fy, out fz); hy = Math.Atan2(-fx, -fz) / Deg; hp = Math.Asin(Clamp(fy, -1, 1)) / Deg; }
            s.Yaw = hy; s.Pitch = Clamp(hp, -25, 35);
            double angW = 2 * Math.Atan(s.Width / 2 / s.Dist) / Deg + 4;
            for (int guard = 0; guard < 8; guard++)
            {
                bool clash = false;
                foreach (var o in Snapshot()) if (!o.Minimized && Math.Abs(AngleDiff(o.Yaw, s.Yaw)) < angW * 0.6 && Math.Abs(o.Pitch - s.Pitch) < 15) clash = true;
                if (!clash) break;
                s.Yaw -= angW;   // to the right
            }
        }

        /// <summary>"+": attach the next free virtual monitor and float it where you're looking.</summary>
        void AddScreen()
        {
            if (m_Busy) return;
            var outs = VirtualScreens.List();
            if (outs.Count == 0) { Toast("No virtual screens driver: run setup to install it"); return; }
            var free = outs.Find(o => !o.Attached);
            if (free == null) { Toast("All " + outs.Count + " virtual screens are already open"); return; }
            m_Busy = true;
            m_Layout.Remove("virtual:" + free.DeviceName);   // a fresh screen goes where you're looking
            Task.Factory.StartNew(() =>
            {
                bool ok = VirtualScreens.Attach(free.DeviceName);
                if (!ok) Log.W("Add screen failed: " + VirtualScreens.LastError);
                Thread.Sleep(900);
                if (m_MainDevice != null) DisplayConfig.EnsurePrimary(m_MainDevice, Log.W);
                BeginInvoke((Action)(() => { RefreshScreens(); m_Busy = false; Toast(ok ? "New screen added" : "Could not add a screen"); SaveLayout(); SyncWindowsLayout(); }));
            });
        }

        void CloseScreen(ScreenWin s)
        {
            if (!s.Virtual || s == MainScreen()) { s.Minimized = true; m_DockStamp = null; Toast("The main screen can't be closed; minimized to the dock"); return; }
            if (m_Busy) return;
            m_Busy = true;
            string dev = s.Mon.DeviceName;
            LayoutStore.Entry le; if (m_Layout.TryGetValue(s.Key, out le)) le.Open = false;
            Task.Factory.StartNew(() =>
            {
                VirtualScreens.Detach(dev);
                Thread.Sleep(700);
                if (m_MainDevice != null) DisplayConfig.EnsurePrimary(m_MainDevice, Log.W);
                BeginInvoke((Action)(() => { RefreshScreens(); m_Busy = false; Toast("Screen closed (its windows moved to your other screens)"); SaveLayout(); }));
            });
        }

        void SaveLayout()
        {
            foreach (var s in Snapshot())
                m_Layout[s.Key] = new LayoutStore.Entry { Yaw = s.Maximized ? s.SYaw : s.Yaw, Pitch = s.Maximized ? s.SPitch : s.Pitch, Dist = s.Maximized ? s.SDist : s.Dist, Width = s.Maximized ? s.SWidth : s.Width, Minimized = s.Minimized, Locked = s.Locked, Open = true };
            foreach (var kv in m_Layout) if (kv.Key.StartsWith("virtual:") && Snapshot().Find(s => s.Key == kv.Key) == null) kv.Value.Open = false;
            LayoutStore.Save(m_Layout);
        }

        void PushPull(double k)
        {
            var s = m_Hover.S;
            var list = s != null ? new List<ScreenWin> { s } : Snapshot();
            foreach (var x in list) if (!x.Locked) x.Dist = Clamp(x.Dist * k, 0.4, 6);
            Toast(k > 1 ? "Farther" : "Closer");
        }

        /// <summary>
        /// Residual yaw drift can't be measured directly (the Air has no usable absolute heading reference), but
        /// while you work on the main screen your gaze centres on it on average. Every 10 s, if your gaze rested
        /// on the main screen for at least half that time, shift the whole scene a twelfth of the way toward your
        /// average gaze - at most 0.5 deg per 10 s (3 deg/min), applied invisibly slowly. Side screens, fast head
        /// motion and dragging don't count.
        /// </summary>
        void AbsorbDrift(double dt)
        {
            double fx, fy, fz; m_Rel.Rotate(0, 0, -1, out fx, out fy, out fz);
            double hy = Math.Atan2(-fx, -fz) / Deg, hp = Math.Asin(Clamp(fy, -1, 1)) / Deg;
            double rate = dt > 0 ? Math.Abs(AngleDiff(hy, m_LastHeadYaw)) / dt : 0;
            m_LastHeadYaw = hy;
            var main = MainScreen();
            if (m_Settings.AutoDrift && m_Imu.Calibrated && main != null && !main.Minimized && m_Mode == Mode.Idle && rate < 8 && !Program.FixedHead)
            {
                double halfW = Math.Atan(main.Width / 2 / main.Dist) / Deg * 0.85, halfH = Math.Atan(main.Height / 2 / main.Dist) / Deg * 0.85;
                double ex = AngleDiff(hy, main.Yaw), ey = hp - main.Pitch;
                if (Math.Abs(ex) < halfW && Math.Abs(ey) < halfH) { m_DriftAcc += ex * dt; m_DriftW += dt; }
            }
            m_DriftT += dt;
            if (m_DriftT >= 10)
            {
                if (m_DriftW >= 5) m_DriftPending += Clamp(m_DriftAcc / m_DriftW / 12.0, -0.5, 0.5);
                m_DriftAcc = m_DriftW = m_DriftT = 0;
            }
            double step = Clamp(m_DriftPending, -0.01, 0.01);    // <= 0.6 deg/s: imperceptible
            if (step != 0)
            {
                m_Anchor = (m_Anchor * Quat.AxisAngle(0, 1, 0, step * Deg)).Normalized();
                m_DriftPending -= step; m_DriftTotal += step;
            }
        }

        /// <summary>Lays out all visible screens in front of you on an arc (up to 3 per row).</summary>
        void Arrange()
        {
            double fx, fy, fz; m_Rel.Rotate(0, 0, -1, out fx, out fy, out fz);
            double headYaw = Math.Atan2(-fx, -fz) / Deg;
            var vis = Snapshot().FindAll(s => !s.Minimized);
            vis.Sort((a, b) => b.Yaw.CompareTo(a.Yaw));   // left (bigger yaw) first
            double d = m_Settings.Distance, w = m_Settings.PanelWidth;
            double angW = 2 * Math.Atan(w / 2 / d) / Deg + 3, angH = 2 * Math.Atan(w * 9 / 16 / 2 / d) / Deg + 7;
            int perRow = 3;
            for (int i = 0; i < vis.Count; i++)
            {
                int row = i / perRow, col = i % perRow, inRow = Math.Min(perRow, vis.Count - row * perRow);
                var s = vis[i];
                s.Maximized = false; s.Dist = d; s.Width = w;
                s.Yaw = headYaw + ((inRow - 1) / 2.0 - col) * angW;
                s.Pitch = row * angH;
            }
            Toast("Arranged " + vis.Count + " screen" + (vis.Count == 1 ? "" : "s"));
            Ui(SaveLayout); Ui(SyncWindowsLayout);
        }

        void ToggleMaximize(ScreenWin s)
        {
            if (s.Maximized) { s.Yaw = s.SYaw; s.Pitch = s.SPitch; s.Dist = s.SDist; s.Width = s.SWidth; s.Maximized = false; }
            else
            {
                s.SYaw = s.Yaw; s.SPitch = s.Pitch; s.SDist = s.Dist; s.SWidth = s.Width;
                double fx, fy, fz; m_Rel.Rotate(0, 0, -1, out fx, out fy, out fz);
                s.Yaw = Math.Atan2(-fx, -fz) / Deg; s.Pitch = Clamp(Math.Asin(Clamp(fy, -1, 1)) / Deg, -20, 30);
                s.Dist = 1.0; s.Width = 2 * s.Dist * Math.Tan(m_HFov * 0.45 * Deg);
                s.Maximized = true;
            }
            s.TitleStamp = null;
        }

        // =====================================================================================================
        // AR cursor + interaction (render thread)
        // =====================================================================================================
        static void Dir(double yawDeg, double pitchDeg, out double x, out double y, out double z)
        {
            double cy = Math.Cos(pitchDeg * Deg);
            x = -Math.Sin(yawDeg * Deg) * cy; y = Math.Sin(pitchDeg * Deg); z = -Math.Cos(yawDeg * Deg) * cy;
        }
        static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
        static double AngleDiff(double a, double b) { double d = (a - b) % 360; if (d > 180) d -= 360; if (d < -180) d += 360; return d; }

        /// <summary>Degrees of cursor travel per mouse count: 1 count = 1 screen pixel over a screen.</summary>
        double DegPerCount(ScreenWin s)
        {
            if (s == null) s = MainScreen();
            if (s == null || s.Mon == null) return 0.03;
            return 2 * Math.Atan(s.Width / 2 / s.Dist) / Deg / Math.Max(1, s.Mon.Bounds.Width);
        }

        Hit HitTest()
        {
            double dx, dy, dz; Dir(m_CurYaw, m_CurPitch, out dx, out dy, out dz);
            var best = new Hit { T = double.MaxValue };
            // dock (closest)
            if (m_DockW > 0)
            {
                double lx, ly, t, ddist, dcy, h; Quat dq;
                DockFrame(out dq, out ddist, out dcy, out h);
                if (IntersectPlane(dq, ddist, dx, dy, dz, out lx, out ly, out t))
                {
                    double w = h * m_DockW / m_DockH;
                    double px = (lx / w + 0.5) * m_DockW, py = (0.5 - (ly - dcy) / h) * m_DockH;
                    if (px >= 0 && px <= m_DockW && py >= 0 && py <= m_DockH)
                    {
                        best.OnDock = true; best.T = t;
                        for (int i = 0; i < m_DockRects.Count && i < m_DockItems.Count; i++) if (m_DockRects[i].Contains((float)px, (float)py)) best.Dock = m_DockItems[i].Id;
                        return best;
                    }
                }
            }
            foreach (var s in Snapshot())
            {
                if (s.Minimized || s.Mon == null) continue;
                double lx, ly, t, u, v;
                if (!s.Intersect(dx, dy, dz, out lx, out ly, out t) || t >= best.T) continue;
                var part = s.HitPart(lx, ly, out u, out v);
                if (part == ScreenWin.Part.None) continue;
                best = new Hit { S = s, Part = part, U = u, V = v, Lx = lx, Ly = ly, T = t };
            }
            if (best.S == null) best.T = 1.5;
            return best;
        }

        static bool IntersectPlane(Quat q, double dist, double dx, double dy, double dz, out double lx, out double ly, out double t)
        {
            lx = ly = t = 0;
            double nx, ny, nz; q.Rotate(0, 0, 1, out nx, out ny, out nz);
            double denom = dx * nx + dy * ny + dz * nz;
            if (denom > -1e-6) return false;
            t = -dist / denom;
            double lz; q.Conj().Rotate(dx * t, dy * t, dz * t, out lx, out ly, out lz);
            return true;
        }

        /// <summary>
        /// The dock hangs just below the main screen (and moves with it). If the main screen is minimized,
        /// it stays where the main screen would be. Its size scales with distance so it looks the same size.
        /// Returns the plane pose/distance and the dock's centre height (cy) and height (h) in that plane.
        /// </summary>
        void DockFrame(out Quat q, out double dist, out double cy, out double h)
        {
            var main = MainScreen();
            double yaw = 0, pitch = 0, d = m_Settings.Distance, screenH = m_Settings.PanelWidth * 9 / 16;
            if (main != null && !main.Minimized) { yaw = main.Yaw; pitch = main.Pitch; d = main.Dist; screenH = main.Height; }
            q = Quat.AxisAngle(0, 1, 0, yaw * Deg) * Quat.AxisAngle(1, 0, 0, pitch * Deg);
            dist = d * 0.995;                       // a hair in front so it's never hidden by the screen
            h = DockHeight * d / DockDist;
            cy = -(screenH / 2 + h * 0.35 + h / 2);
        }

        void SetRealCursor(ScreenWin s, double u, double v)
        {
            var b = s.Mon.Bounds;
            int x = b.Left + (int)(Clamp(u, 0, 0.9999) * b.Width), y = b.Top + (int)(Clamp(v, 0, 0.9999) * b.Height);
            // (compare with where the real cursor actually is: something else may have moved it since)
            POINT now;
            if (x == m_LastSetCursor.X && y == m_LastSetCursor.Y && Native.GetCursorPos(out now) && now.X == x && now.Y == y) return;
            Native.SetCursorPos(x, y);
            m_LastSetCursor = new POINT { X = x, Y = y };
        }

        /// <summary>Keeps the cursor inside the view: turning your head drags it along.</summary>
        void ClampCursorToView()
        {
            double dx, dy, dz; Dir(m_CurYaw, m_CurPitch, out dx, out dy, out dz);
            double lx, ly, lz; m_Rel.Conj().Rotate(dx, dy, dz, out lx, out ly, out lz);
            double ly0 = Math.Atan2(-lx, -lz) / Deg, lp0 = Math.Asin(Clamp(ly, -1, 1)) / Deg;
            double maxY = m_HFov / 2 * 0.94, maxP = m_VFov / 2 * 0.92;
            double ly1 = Clamp(ly0, -maxY, maxY), lp1 = Clamp(lp0, -maxP, maxP);
            if (ly1 == ly0 && lp1 == lp0 && lz < 0) return;
            double wx, wy, wz; Dir(ly1, lp1, out dx, out dy, out dz); m_Rel.Rotate(dx, dy, dz, out wx, out wy, out wz);
            m_CurYaw = Math.Atan2(-wx, -wz) / Deg; m_CurPitch = Math.Asin(Clamp(wy, -1, 1)) / Deg;
        }

        long m_LastFollowMs = -10000; bool m_Following;

        /// <summary>
        /// While an administrator window has the focus Windows hides the physical mouse from us (no hook calls, no
        /// raw input): the real pointer moves by itself and the AR cursor would freeze. So in that state, whenever
        /// the real pointer is somewhere we didn't put it, the AR cursor goes to it instead of the other way round.
        /// Clicks then reach the window directly. Returns true if it moved the AR cursor this frame.
        /// </summary>
        bool FollowRealPointer()
        {
            long nowMs = m_Clock.ElapsedMilliseconds;
            if (m_Mode != Mode.Idle || !m_Input.Events.IsEmpty || !ElevatedWindows.ForegroundBlocked())
            {
                if (m_Following && nowMs - m_LastFollowMs > 2000) { m_Following = false; Log.W("AR cursor is driven by the mouse again"); }
                return false;
            }
            POINT p;
            if (!Native.GetCursorPos(out p) || (p.X == m_LastSetCursor.X && p.Y == m_LastSetCursor.Y)) return false;
            m_LastFollowMs = nowMs;
            if (!m_Following) { m_Following = true; Log.W("An administrator window has the focus (Windows hides the mouse from the viewer): the AR cursor follows the Windows pointer"); }
            var s = ScreenAt(p);
            if (s == null || s.Minimized)
            {
                // on the glasses' own display the UI thread jumps it across; on a PC screen that isn't shown in AR, push it back
                var g = m_Glasses.Bounds;
                bool onGlasses = p.X >= g.Left && p.X < g.Right && p.Y >= g.Top && p.Y < g.Bottom;
                if (!onGlasses && m_Settings.ConfineMouse && ScreenAt(m_LastSetCursor) != null) Native.SetCursorPos(m_LastSetCursor.X, m_LastSetCursor.Y);
                return true;
            }
            var b = s.Mon.Bounds;
            double lx = ((p.X - b.Left + 0.5) / b.Width - 0.5) * s.Width, ly = (0.5 - (p.Y - b.Top + 0.5) / b.Height) * s.Height;
            double px, py, pz; s.Pose.Rotate(lx, ly, -s.Dist, out px, out py, out pz);
            double len = Math.Sqrt(px * px + py * py + pz * pz);
            m_CurYaw = Math.Atan2(-px / len, -pz / len) / Deg; m_CurPitch = Math.Asin(Clamp(py / len, -1, 1)) / Deg;
            m_LastSetCursor = p;
            return true;
        }

        void ProcessInput()
        {
            InputEvt ev;
            int n = 0;
            while (n++ < 400 && m_Input != null && m_Input.Events.TryDequeue(out ev))
            {
                switch (ev.Kind)
                {
                    case InputKind.Move: OnMove(ev.Dx, ev.Dy); break;
                    case InputKind.Button: if (ev.Down) OnDown(ev.Button); else OnUp(ev.Button); break;
                    case InputKind.Wheel: OnWheel(ev.Wheel, ev.Horizontal); break;
                }
            }
        }

        void OnMove(int dx, int dy)
        {
            ScreenWin over = m_Mode == Mode.ContentPress ? m_Target : (m_Hover.Part == ScreenWin.Part.Content ? m_Hover.S : null);
            double k = DegPerCount(over) * m_Settings.MouseSpeed;
            m_CurYaw -= dx * k; m_CurPitch = Clamp(m_CurPitch - dy * k, -85, 85);
            ClampCursorToView();
            m_Hover = HitTest();
            switch (m_Mode)
            {
                case Mode.ContentPress:
                    if (m_Hover.Part == ScreenWin.Part.Content && m_Hover.S != null) SetRealCursor(m_Hover.S, m_Hover.U, m_Hover.V);
                    else
                    {
                        // Dragging outside the screen: clamp to the pressed screen's edge.
                        double dxw, dyw, dzw, lx, ly, t; Dir(m_CurYaw, m_CurPitch, out dxw, out dyw, out dzw);
                        if (m_Target.Intersect(dxw, dyw, dzw, out lx, out ly, out t)) SetRealCursor(m_Target, lx / m_Target.Width + 0.5, 0.5 - ly / m_Target.Height);
                    }
                    break;
                case Mode.Move:
                    m_Target.Yaw = m_CurYaw + m_OffYaw; m_Target.Pitch = Clamp(m_CurPitch + m_OffPitch, -70, 75);
                    break;
                case Mode.Resize:
                {
                    double dxw, dyw, dzw, lx, ly, t; Dir(m_CurYaw, m_CurPitch, out dxw, out dyw, out dzw);
                    if (!m_Target.Intersect(dxw, dyw, dzw, out lx, out ly, out t)) break;
                    double a = m_Target.Aspect, w = m_Target.Width;
                    if (m_TargetPart == ScreenWin.Part.EdgeL || m_TargetPart == ScreenWin.Part.EdgeR) w = 2 * Math.Abs(lx);
                    else if (m_TargetPart == ScreenWin.Part.EdgeB) w = 2 * Math.Abs(ly) / a;
                    else w = Math.Max(2 * Math.Abs(lx), 2 * Math.Abs(ly) / a);
                    m_Target.Width = Clamp(w, 0.3, 4.0); m_Target.Maximized = false; m_Target.TitleStamp = null;
                    break;
                }
                case Mode.Idle:
                    if (m_Hover.Part == ScreenWin.Part.Content && m_Hover.S != null) SetRealCursor(m_Hover.S, m_Hover.U, m_Hover.V);
                    break;
            }
        }

        void OnDown(int button)
        {
            m_Hover = HitTest();
            if (m_Mode == Mode.ContentPress) { InjectAt(button, true); m_ContentButtons |= 1 << button; return; }
            if (m_Mode != Mode.Idle) return;
            if (PopUnderCursor())
            {
                // pop! (the click goes no further; its release is swallowed by the Press mode)
                m_Mode = Mode.Press; m_PressId = "bubble"; m_Target = null;
                return;
            }
            var h = m_Hover;
            if (h.OnDock) { if (button == 0 && h.Dock != null) { m_Mode = Mode.Press; m_PressId = "dock:" + h.Dock; } return; }
            if (h.S == null) return;
            var s = h.S;
            switch (h.Part)
            {
                case ScreenWin.Part.Content:
                    SetRealCursor(s, h.U, h.V);
                    m_Mode = Mode.ContentPress; m_Target = s; m_ContentButtons = 1 << button;
                    InjectAt(button, true);
                    break;
                case ScreenWin.Part.Title:
                    if (button != 0) return;
                    if (s == m_LastTitleScreen && (DateTime.UtcNow - m_LastTitleClick).TotalMilliseconds < 400) { ToggleMaximize(s); m_LastTitleClick = DateTime.MinValue; return; }
                    m_LastTitleClick = DateTime.UtcNow; m_LastTitleScreen = s;
                    if (s.Locked) { Toast("Screen is locked (click the padlock to unlock)"); return; }
                    m_Mode = Mode.Move; m_Target = s;
                    m_OffYaw = s.Yaw - m_CurYaw; m_OffPitch = s.Pitch - m_CurPitch;
                    if (s.Maximized) { s.Maximized = false; s.Width = s.SWidth; s.Dist = s.SDist; s.TitleStamp = null; }
                    break;
                case ScreenWin.Part.Lock: case ScreenWin.Part.Minimize: case ScreenWin.Part.Maximize: case ScreenWin.Part.Close:
                case ScreenWin.Part.Pull: case ScreenWin.Part.Push: case ScreenWin.Part.DistPct:
                    if (button == 0) { m_Mode = Mode.Press; m_PressId = h.Part + ":" + s.Key; m_Target = s; }
                    break;
                default:
                    if (ScreenWin.IsEdge(h.Part) && button == 0 && !s.Locked) { m_Mode = Mode.Resize; m_Target = s; m_TargetPart = h.Part; }
                    break;
            }
        }

        /// <summary>Ends any press/drag, releasing buttons we told Windows were down.</summary>
        void ResetInteraction()
        {
            if (m_Mode == Mode.ContentPress)
                for (int b = 0; b < 5; b++) if ((m_ContentButtons & (1 << b)) != 0) InputHook.InjectButton(b, false);
            m_ContentButtons = 0;
            if (m_Mode != Mode.Idle) { m_Mode = Mode.Idle; m_Target = null; }
        }

        void InjectAt(int button, bool down)
        {
            InputHook.InjectButton(button, down);
        }

        void OnUp(int button)
        {
            m_Hover = HitTest();
            switch (m_Mode)
            {
                case Mode.ContentPress:
                    if ((m_ContentButtons & (1 << button)) != 0) { InjectAt(button, false); m_ContentButtons &= ~(1 << button); }
                    if (m_ContentButtons == 0) { m_Mode = Mode.Idle; m_Target = null; }
                    break;
                case Mode.Move: case Mode.Resize:
                    if (button == 0) { bool moved = m_Mode == Mode.Move; m_Mode = Mode.Idle; m_Target = null; Ui(SaveLayout); if (moved) Ui(SyncWindowsLayout); }
                    break;
                case Mode.Press:
                    if (button != 0) return;
                    string id = m_PressId; m_Mode = Mode.Idle;
                    string now = m_Hover.OnDock ? (m_Hover.Dock != null ? "dock:" + m_Hover.Dock : null) : (m_Hover.S != null ? m_Hover.Part + ":" + m_Hover.S.Key : null);
                    if (id == now) Activate(id, m_Target);
                    m_Target = null;
                    break;
            }
        }

        void OnWheel(int delta, bool horizontal)
        {
            m_Hover = HitTest();
            bool onBar = m_Hover.Part == ScreenWin.Part.Title || m_Hover.Part == ScreenWin.Part.Pull || m_Hover.Part == ScreenWin.Part.Push || m_Hover.Part == ScreenWin.Part.DistPct;
            if (m_Mode == Mode.Move || (m_Mode == Mode.Idle && m_Hover.S != null && onBar))
            {
                var s = m_Mode == Mode.Move ? m_Target : m_Hover.S;
                if (s.Locked || horizontal) return;
                s.Dist = Clamp(s.Dist * (delta > 0 ? 1.08 : 1 / 1.08), 0.4, 6);   // wheel up pushes away
                s.Maximized = false;
                return;
            }
            if (m_Mode == Mode.ContentPress || (m_Hover.S != null && m_Hover.Part == ScreenWin.Part.Content))
            {
                if (m_Mode != Mode.ContentPress) SetRealCursor(m_Hover.S, m_Hover.U, m_Hover.V);
                InputHook.InjectWheel(delta, horizontal);
            }
        }

        void Ui(Action a) { try { BeginInvoke(a); } catch { } }

        void Activate(string id, ScreenWin s)
        {
            if (id.StartsWith("dock:"))
            {
                string d = id.Substring(5);
                if (d == "add") Ui(AddScreen);
                else if (d == "arrange") m_ArrangeRequested = true;
                else if (d == "recenter") m_RecenterRequested = true;
                else if (d == "settings") Ui(ShowSettings);
                else if (d == "mouse") Ui(() => SetArMouse(false));
                else if (d.StartsWith("restore:"))
                {
                    var r = Snapshot().Find(x => x.Key == d.Substring(8));
                    if (r != null) { r.Minimized = false; m_DockStamp = null; Ui(SaveLayout); }
                }
                return;
            }
            if (s == null) return;
            if (id.StartsWith("Lock:")) { s.Locked = !s.Locked; s.TitleStamp = null; Ui(SaveLayout); }
            else if (id.StartsWith("Minimize:")) { s.Minimized = true; m_DockStamp = null; Ui(SaveLayout); }
            else if (id.StartsWith("Maximize:")) ToggleMaximize(s);
            else if (id.StartsWith("Close:")) Ui(() => CloseScreen(s));
            else if (id.StartsWith("Pull:")) StepDistance(s, -10);
            else if (id.StartsWith("Push:")) StepDistance(s, +10);
            else if (id.StartsWith("DistPct:")) StepDistance(s, 0);
        }

        /// <summary>The screen's distance as a percentage of the default distance (Settings), as its title bar shows it.</summary>
        int DistPercent(ScreenWin s)
        {
            double d0 = m_Settings.Distance > 0.2 ? m_Settings.Distance : 1.6;
            return (int)Math.Round(s.Dist / d0 * 100);
        }

        /// <summary>Title-bar Pull/Push (for trackpads; the mouse wheel on the bar still works): steps the distance by
        /// stepPct of the default, snapped to whole steps. 0 = back to 100%.</summary>
        void StepDistance(ScreenWin s, int stepPct)
        {
            if (s.Locked) { Toast("Screen is locked (click the padlock to unlock)"); return; }
            double d0 = m_Settings.Distance > 0.2 ? m_Settings.Distance : 1.6;
            int pct = stepPct == 0 ? 100 : (int)(Math.Round(DistPercent(s) / 10.0) * 10) + stepPct;
            if (s.Maximized) { s.Maximized = false; s.Width = s.SWidth; }
            s.Dist = Clamp(d0 * pct / 100.0, 0.4, 6);
            s.TitleStamp = null;
            Ui(SaveLayout);
        }

        // =====================================================================================================
        // rendering
        // =====================================================================================================
        static uint NewContentTexture()
        {
            uint t; Gl.GenTextures(1, out t);
            Gl.BindTexture(Gl.TEXTURE_2D, t);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.MinFilter);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);

            Gl.TexParameterf(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_ANISOTROPY, 8f);
            Gl.GetError();
            return t;
        }

        void RenderLoop()
        {
            IntPtr hdc = IntPtr.Zero, ctx = IntPtr.Zero;
            try
            {
                hdc = Native.GetDC(Handle);
                ctx = Gl.CreateContext(hdc);
                bool vsync = Gl.SetSwapInterval(1);
                Gl.LoadExtensions();
                Log.W("Mipmaps: " + (Gl.HasGenerateMipmap ? "rebuilt explicitly (glGenerateMipmap)" : "off (driver has no glGenerateMipmap)"));
                Log.W(string.Format("OpenGL: {0} / {1} / {2}, vsync={3}", Gl.GetString(Gl.VENDOR), Gl.GetString(Gl.RENDERER), Gl.GetString(Gl.VERSION), vsync));
                IntPtr font = Gl.CreateFont(-30, 0, 0, 0, 600, 0, 0, 0, 0, 0, 0, 5, 0, "Segoe UI");
                Native.SelectObject(hdc, font);
                uint fontBase = Gl.GenLists(96);
                Gl.wglUseFontBitmapsW(hdc, 32, 96, fontBase);
                Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
                using (var a = Chrome.Arrow()) m_ArrowTex = Chrome.Upload(a, 0);

                int W = m_Glasses.Bounds.Width, H = m_Glasses.Bounds.Height;
                int eyes = m_Stereo ? 2 : 1, EW = W / eyes;
                var fpsWatch = Stopwatch.StartNew(); int frames = 0;
                double nextLog = 1, lastT = m_Clock.Elapsed.TotalSeconds;
                while (m_Running)
                {
                    double nowT = m_Clock.Elapsed.TotalSeconds, dt = Math.Min(0.1, nowT - lastT); lastT = nowT;
                    m_VFov = m_Settings.FovV; m_HFov = 2 * Math.Atan(Math.Tan(m_VFov / 2 * Deg) * EW / H) / Deg;

                    // free GL resources of removed screens
                    lock (m_Lock)
                    {
                        foreach (var d in m_Dead) { if (d.Tex != 0) { uint t = d.Tex; Gl.DeleteTextures(1, ref t); } if (d.TitleTex != 0) { uint t = d.TitleTex; Gl.DeleteTextures(1, ref t); } }
                        m_Dead.Clear();
                    }
                    var screens = Snapshot();

                    // textures
                    double upStart = m_FrameClock.Elapsed.TotalMilliseconds;
                    foreach (var s in screens)
                    {
                        if (s.Feed == null) continue;
                        if (s.Tex == 0) s.Tex = NewContentTexture();
                        var ss = s;
                        Action<IntPtr, int, int> upload = (ptr, w, h) =>
                        {
                            Gl.BindTexture(Gl.TEXTURE_2D, ss.Tex);
                            if (w != ss.TexW || h != ss.TexH) { Gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGB8, w, h, 0, Gl.BGRA, Gl.UNSIGNED_BYTE, ptr); ss.TexW = w; ss.TexH = h; }
                            else Gl.TexSubImage2D(Gl.TEXTURE_2D, 0, 0, 0, w, h, Gl.BGRA, Gl.UNSIGNED_BYTE, ptr);
                            Gl.GenerateMipmap();   // keep every mip level in step with the new image (no ghosting)
                        };
                        IntPtr fp; int fw, fh; bool got;
                        if (!s_NoPacing && s.Feed.TakePacedFrame(out fp, out fw, out fh)) { upload(fp, fw, fh); got = true; }   // paced, zero-copy
                        else if (s.Feed.TakeFrame(ref s.Seen, out fp, out fw, out fh)) { upload(fp, fw, fh); got = true; }      // zero-copy
                        else { long before = s.Seen; s.Feed.TryUpload(ref s.Seen, upload); got = s.Seen != before; }
                        if (s == MainScreen())
                        {
                            m_SinceNewFrame++;
                            if (got) { m_Cadence[Math.Min(4, m_SinceNewFrame)]++; m_SinceNewFrame = 0; }
                        }
                        UpdateTitle(s);
                    }
                    m_UploadMs += m_FrameClock.Elapsed.TotalMilliseconds - upStart; m_UploadCount++;
                    UpdateDock(screens);

                    // head pose: sampled AFTER the uploads, right before drawing, so the time from 'measure head' to
                    // 'show image' stays constant even while video frames are being uploaded (otherwise: judder)
                    Quat head = Program.FixedHead ? Quat.AxisAngle(1, 0, 0, Program.FixedPitch * Deg) : m_Imu.GetPose(m_Settings.PredictMs / 1000.0);   // --fixed-head: tests without wearing the glasses
                    if (m_RecenterRequested)
                    {
                        m_RecenterRequested = false; m_Anchor = Quat.AxisAngle(0, 1, 0, head.Yaw()); m_DriftPending = 0;
                        m_CurYaw = 0; m_CurPitch = -2;
                    }
                    m_Stab.Mode = (Stabilizer.Level)Math.Max(0, Math.Min(3, m_Settings.Stabilization));
                    if (!Program.FixedHead) head = m_Stab.Update(head, dt);
                    m_Rel = m_Anchor.Conj() * head;
                    if (m_ArrangeRequested) { m_ArrangeRequested = false; Arrange(); }
                    AbsorbDrift(dt);


                    // input
                    if (m_Input != null && m_Input.Active) { if (!FollowRealPointer()) { ProcessInput(); ClampCursorToView(); } m_Hover = HitTest(); }
                    else { InputEvt e; while (m_Input != null && m_Input.Events.TryDequeue(out e)) { } ResetInteraction(); }
                    UpdateBubble(dt);
                    // over a UAC prompt / admin window our injected clicks are dropped: let the real ones through
                    if (m_Input != null)
                    {
                        bool pass = false;
                        if (m_Input.Active && m_Mode == Mode.Idle && m_HoverBubble == null && m_Hover.S != null && m_Hover.Part == ScreenWin.Part.Content)
                        { POINT c; Native.GetCursorPos(out c); pass = ElevatedWindows.IsBlockedAt(c.X, c.Y); }
                        if (pass != m_Input.PassClicks) { m_Input.PassClicks = pass; Log.W(pass ? "Cursor over an administrator window: real clicks go straight through" : "Cursor left the administrator window"); }
                    }

                    // draw
                    double drawStart = m_FrameClock.Elapsed.TotalMilliseconds;
                    Gl.Viewport(0, 0, W, H);
                    Gl.ClearColor(0, 0, 0, 1);
                    Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
                    double[] view = m_Rel.Conj().ToGlMatrix();
                    for (int e = 0; e < eyes; e++)
                    {
                        Gl.Viewport(e * EW, 0, EW, H);
                        Gl.MatrixMode(Gl.PROJECTION); Gl.LoadIdentity();
                        double near = 0.05, top = near * Math.Tan(m_VFov / 2 * Deg), right = top * EW / H;
                        // 3D: each eye's camera sits half the eye distance to the side (e 0 = left eye). An
                        // off-axis frustum makes both images line up exactly at 1/StereoConverge metres, and the
                        // vertical offset (split between the eyes) lines them up vertically.
                        double sx = 0, sy = 0, half = m_Stereo ? m_Settings.IpdMm / 2000.0 : 0;
                        if (m_Stereo)
                        {
                            sx = (e == 0 ? 1 : -1) * half * m_Settings.StereoConverge * near;
                            sy = (e == 0 ? 0.5 : -0.5) * m_Settings.StereoVOffset * (2 * top / H);
                        }
                        Gl.Frustum(-right + sx, right + sx, -top + sy, top + sy, near, 100);
                        Gl.MatrixMode(Gl.MODELVIEW); Gl.LoadIdentity();
                        if (m_Stereo) Gl.Translated((e == 0 ? 1 : -1) * half, 0, 0);
                        Gl.PushMatrix();
                        Gl.MultMatrixd(view);
                        DrawScene(screens);
                        Gl.PopMatrix();
                        if (m_Calibrating3D) DrawStereoCalibration(e, right / near, top / near);
                        if (m_Stereo && m_Settings.StereoEdgeMatch) DrawEdgeMatch(e, EW, H, right / near);
                        DrawHud(EW, H, fontBase);
                    }

                    if (m_ReadbackRequested) { m_ReadbackRequested = false; SaveReadback(W, H); }
                    double swapStart = m_FrameClock.Elapsed.TotalMilliseconds;
                    m_DrawMs += swapStart - drawStart;
                    Gl.SwapBuffers(hdc);
                    m_SwapMs += m_FrameClock.Elapsed.TotalMilliseconds - swapStart;
                    if (!vsync) Thread.Sleep(10);
                    double frameMs = m_FrameClock.Elapsed.TotalMilliseconds - m_LastSwapMs; m_LastSwapMs = m_FrameClock.Elapsed.TotalMilliseconds;
                    if (frameMs > 25) m_LateFrames++;
                    if (frameMs > m_WorstFrameMs) m_WorstFrameMs = frameMs;

                    frames++;
                    if (fpsWatch.ElapsedMilliseconds >= 1000) { RenderFps = frames * 1000.0 / fpsWatch.ElapsedMilliseconds; frames = 0; fpsWatch.Restart(); }
                    if (m_Clock.Elapsed.TotalSeconds >= nextLog) { nextLog += m_SelfTestSeconds > 0 ? 1 : 30; LogStats(); }
                }
            }
            catch (Exception ex) { Log.W("Render thread failed: " + ex); }
            finally
            {
                if (ctx != IntPtr.Zero) { Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); Gl.wglDeleteContext(ctx); }
                if (hdc != IntPtr.Zero) Native.ReleaseDC(Handle, hdc);
            }
        }

        void UpdateTitle(ScreenWin s)
        {
            var hp = m_Hover.S == s ? m_Hover.Part : ScreenWin.Part.None;
            Chrome.TitleHit th = Chrome.TitleHit.None;
            if (hp == ScreenWin.Part.Lock) th = Chrome.TitleHit.Lock; else if (hp == ScreenWin.Part.Minimize) th = Chrome.TitleHit.Minimize;
            else if (hp == ScreenWin.Part.Maximize) th = Chrome.TitleHit.Maximize; else if (hp == ScreenWin.Part.Close) th = Chrome.TitleHit.Close;
            else if (hp == ScreenWin.Part.Title) th = Chrome.TitleHit.Bar;
            else if (hp == ScreenWin.Part.Pull) th = Chrome.TitleHit.Pull; else if (hp == ScreenWin.Part.Push) th = Chrome.TitleHit.Push;
            else if (hp == ScreenWin.Part.DistPct) th = Chrome.TitleHit.DistPct;
            bool active = m_Target == s && m_Mode == Mode.Move;
            int pxW = (int)(Chrome.TitlePx * s.Width / s.TitleH);
            int pct = DistPercent(s);
            string res = s.Mon != null ? s.Mon.Bounds.Width + "×" + s.Mon.Bounds.Height : "";
            string stamp = s.Title + "|" + s.Locked + "|" + th + "|" + s.Maximized + "|" + active + "|" + pxW / 32 + "|" + pct;
            if (stamp == s.TitleStamp && s.TitleTex != 0) return;
            s.TitleStamp = stamp; s.TitlePxW = pxW;
            using (var bmp = Chrome.TitleBar(pxW, s.Title + "   " + res, s.Locked, th, s.Maximized, active, pct)) s.TitleTex = Chrome.Upload(bmp, s.TitleTex);
        }

        void UpdateDock(List<ScreenWin> screens)
        {
            var items = new List<Chrome.DockItem>
            {
                new Chrome.DockItem { Id = "add", Label = "+  New screen", Accent = true, Disabled = m_Busy },
                new Chrome.DockItem { Id = "arrange", Label = "Arrange" },
                new Chrome.DockItem { Id = "recenter", Label = "Recenter" },
                new Chrome.DockItem { Id = "settings", Label = "Settings" },
            };
            foreach (var s in screens) if (s.Minimized) items.Add(new Chrome.DockItem { Id = "restore:" + s.Key, Label = "▴ " + s.Title });
            string hover = m_Hover.OnDock ? m_Hover.Dock : null;
            string status = m_Imu.Calibrated ? (m_Input != null && m_Input.Active ? "Mouse: glasses" : "Mouse: PC") : "Calibrating";
            var sb = new System.Text.StringBuilder(status + "|" + hover + "|" + m_Busy);
            foreach (var it in items) sb.Append('|').Append(it.Id);
            string stamp = sb.ToString();
            if (stamp == m_DockStamp && m_DockTex != 0) return;
            m_DockStamp = stamp;
            List<RectangleF> rects;
            using (var bmp = Chrome.Dock(items, hover, status, out rects)) { m_DockTex = Chrome.Upload(bmp, m_DockTex); m_DockW = bmp.Width; m_DockH = bmp.Height; }
            m_DockRects = rects; m_DockItems = items;
        }

        static void Quad(double x0, double y0, double x1, double y1, double z)
        {
            Gl.Begin(Gl.QUADS);
            Gl.TexCoord2d(0, 0); Gl.Vertex3d(x0, y1, z);
            Gl.TexCoord2d(0, 1); Gl.Vertex3d(x0, y0, z);
            Gl.TexCoord2d(1, 1); Gl.Vertex3d(x1, y0, z);
            Gl.TexCoord2d(1, 0); Gl.Vertex3d(x1, y1, z);
            Gl.End();
        }

        static void Outline(double x0, double y0, double x1, double y1, double z, double r, double g, double b, float width)
        {
            Gl.Disable(Gl.TEXTURE_2D);
            Gl.Color4d(r, g, b, 1); Gl.LineWidth(width);
            Gl.Begin(Gl.LINE_LOOP); Gl.Vertex3d(x0, y0, z); Gl.Vertex3d(x1, y0, z); Gl.Vertex3d(x1, y1, z); Gl.Vertex3d(x0, y1, z); Gl.End();
            Gl.Color4d(1, 1, 1, 1); Gl.Enable(Gl.TEXTURE_2D);
        }

        void DrawScene(List<ScreenWin> screens)
        {
            Gl.Enable(Gl.DEPTH_TEST);
            Gl.Enable(Gl.TEXTURE_2D);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            Gl.Color4d(1, 1, 1, 1);
            foreach (var s in screens)
            {
                if (s.Minimized || s.TexW == 0) continue;
                double w = s.Width, h = s.Height;
                Gl.PushMatrix();
                Gl.MultMatrixd(s.Pose.ToGlMatrix());
                Gl.Translated(0, 0, -s.Dist);
                Gl.BindTexture(Gl.TEXTURE_2D, s.Tex);
                Quad(-w / 2, -h / 2, w / 2, h / 2, 0);
                if (s.TitleTex != 0)
                {
                    Gl.BindTexture(Gl.TEXTURE_2D, s.TitleTex);
                    Quad(-w / 2, h / 2 + s.Gap, w / 2, h / 2 + s.Gap + s.TitleH, 0);
                }
                bool edge = (m_Mode == Mode.Resize && m_Target == s) || (m_Mode == Mode.Idle && m_Hover.S == s && ScreenWin.IsEdge(m_Hover.Part));
                if (edge) Outline(-w / 2, -h / 2, w / 2, h / 2, 0.002, 0.3, 0.65, 1, 5);
                else if (m_Mode == Mode.Move && m_Target == s) Outline(-w / 2, -h / 2, w / 2, h / 2 + s.Gap + s.TitleH, 0.002, 0.3, 0.65, 1, 3);
                Gl.PopMatrix();
            }
            // dock
            if (m_DockTex != 0)
            {
                Quat dq; double ddist, dcy, h;
                DockFrame(out dq, out ddist, out dcy, out h);
                double w = h * m_DockW / m_DockH;
                Gl.PushMatrix();
                Gl.MultMatrixd(dq.ToGlMatrix());
                Gl.Translated(0, 0, -ddist);
                Gl.BindTexture(Gl.TEXTURE_2D, m_DockTex);
                Quad(-w / 2, dcy - h / 2, w / 2, dcy + h / 2, 0);
                Gl.PopMatrix();
            }
            DrawBubbles();
            // cursor (on top of everything)
            Gl.Disable(Gl.DEPTH_TEST);
            if (m_Input != null && m_Input.Active) DrawCursor();
            Gl.Disable(Gl.BLEND);
            Gl.Disable(Gl.TEXTURE_2D);
        }

        void DrawCursor()
        {
            uint tex = m_ArrowTex; int hx = 0, hy = 0;
            bool overContent = m_Mode == Mode.ContentPress || (m_Hover.S != null && m_Hover.Part == ScreenWin.Part.Content);
            if (overContent)
            {
                var ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (Native.GetCursorInfo(ref ci) && (ci.flags & 1) != 0 && ci.hCursor != IntPtr.Zero)
                {
                    uint t;
                    if (!m_CursorTex.TryGetValue(ci.hCursor, out t))
                    {
                        int ax, ay;
                        using (var bmp = Chrome.FromCursor(ci.hCursor, out ax, out ay)) if (bmp != null) { t = Chrome.Upload(bmp, 0); m_CursorHot[ci.hCursor] = new Point(ax, ay); }
                        m_CursorTex[ci.hCursor] = t;
                    }
                    if (t != 0) { tex = t; hx = m_CursorHot[ci.hCursor].X; hy = m_CursorHot[ci.hCursor].Y; }
                }
            }
            double depth = Math.Min(m_Hover.T, 8) * 0.995;
            var hb = m_HoverBubble; if (hb != null) depth = Math.Min(depth, hb.Dist * 0.98);
            double dx, dy, dz; Dir(m_CurYaw, m_CurPitch, out dx, out dy, out dz);
            double size = depth * Math.Tan(1.9 * Deg);              // 64 px sprite ~1.9 deg tall
            double rx, ry, rz, ux, uy, uz; m_Rel.Rotate(1, 0, 0, out rx, out ry, out rz); m_Rel.Rotate(0, 1, 0, out ux, out uy, out uz);
            double px = dx * depth - (rx * hx - ux * hy) * size / 64, py = dy * depth - (ry * hx - uy * hy) * size / 64, pz = dz * depth - (rz * hx - uz * hy) * size / 64;
            Gl.BindTexture(Gl.TEXTURE_2D, tex);
            Gl.Begin(Gl.QUADS);
            Gl.TexCoord2d(0, 0); Gl.Vertex3d(px, py, pz);
            Gl.TexCoord2d(0, 1); Gl.Vertex3d(px - ux * size, py - uy * size, pz - uz * size);
            Gl.TexCoord2d(1, 1); Gl.Vertex3d(px - ux * size + rx * size, py - uy * size + ry * size, pz - uz * size + rz * size);
            Gl.TexCoord2d(1, 0); Gl.Vertex3d(px + rx * size, py + ry * size, pz + rz * size);
            Gl.End();
        }

        // =====================================================================================================
        // 3D calibration and edge matching
        // =====================================================================================================
        volatile bool m_Calibrating3D;
        float m_CalSavedConverge, m_CalSavedVOffset, m_CalSavedIpd; bool m_CalSavedEdges;

        /// <summary>Distance of the main screen: the depth the edge matching and the test pattern use.</summary>
        double MainDist() { var m = MainScreen(); return m != null && m.Dist > 0.2 ? m.Dist : m_Settings.Distance; }

        void StartCalibration3D()
        {
            if (m_Calibrating3D) return;
            if (!m_Stereo) { Toast("3D calibration needs side-by-side 3D: turn it on in Settings"); return; }
            m_CalSavedConverge = m_Settings.StereoConverge; m_CalSavedVOffset = m_Settings.StereoVOffset;
            m_CalSavedIpd = m_Settings.IpdMm; m_CalSavedEdges = m_Settings.StereoEdgeMatch;
            for (int i = 0; i < s_CalKeys.Length; i++) RegisterHotKey(Handle, (int)s_CalKeys[i], 0, s_CalVk[i]);
            m_Calibrating3D = true;
            Log.W("3D calibration started");
        }

        void EndCalibration3D(bool save)
        {
            if (!m_Calibrating3D) return;
            m_Calibrating3D = false;
            foreach (var k in s_CalKeys) UnregisterHotKey(Handle, (int)k);
            if (!save)
            {
                m_Settings.StereoConverge = m_CalSavedConverge; m_Settings.StereoVOffset = m_CalSavedVOffset;
                m_Settings.IpdMm = m_CalSavedIpd; m_Settings.StereoEdgeMatch = m_CalSavedEdges;
            }
            m_Settings.Save();
            Toast(save ? "3D calibration saved" : "3D calibration cancelled");
            Log.W(string.Format(CultureInfo.InvariantCulture, "3D calibration {0}: converge {1:F2}/m, vertical {2:F0} px, IPD {3:F0} mm, edges {4}",
                save ? "saved" : "cancelled", m_Settings.StereoConverge, m_Settings.StereoVOffset, m_Settings.IpdMm, m_Settings.StereoEdgeMatch));
        }

        void CalibrationKey(Hk k)
        {
            if (!m_Calibrating3D) return;
            var s = m_Settings;
            switch (k)
            {
                case Hk.CalLeft: s.StereoConverge = (float)Math.Min(2, Math.Round(s.StereoConverge + 0.05, 2)); break;    // line up nearer
                case Hk.CalRight: s.StereoConverge = (float)Math.Max(0, Math.Round(s.StereoConverge - 0.05, 2)); break;   // line up farther
                case Hk.CalUp: s.StereoVOffset = Math.Min(60, s.StereoVOffset + 1); break;
                case Hk.CalDown: s.StereoVOffset = Math.Max(-60, s.StereoVOffset - 1); break;
                case Hk.CalIpdDown: s.IpdMm = Math.Max(50, s.IpdMm - 1); break;
                case Hk.CalIpdUp: s.IpdMm = Math.Min(80, s.IpdMm + 1); break;
                case Hk.CalEdges: s.StereoEdgeMatch = !s.StereoEdgeMatch; break;
                case Hk.CalSnap: s.StereoConverge = (float)Math.Round(1 / MainDist(), 2); break;
                case Hk.CalReset: s.StereoConverge = 0; s.StereoVOffset = 0; s.IpdMm = 63; s.StereoEdgeMatch = true; break;
                case Hk.CalSave: EndCalibration3D(true); break;
                case Hk.CalCancel: EndCalibration3D(false); break;
            }
        }

        /// <summary>
        /// Head-locked test pattern at the main screen's distance, drawn with the eye's own offset so it has the
        /// same depth as the screens: a frame + grid (should look single and sharp) and a horizontal and vertical
        /// line drawn red in the left eye and green in the right eye (should merge into one line).
        /// </summary>
        void DrawStereoCalibration(int eye, double tanX, double tanY)
        {
            double d = MainDist(), hw = 0.85 * tanX * d, hh = 0.75 * tanY * d, z = -d;
            Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.TEXTURE_2D);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            // dim the screens behind the pattern
            Gl.Color4d(0, 0, 0, 0.55);
            Gl.Begin(Gl.QUADS); Gl.Vertex3d(-hw * 1.3, -hh * 1.3, z); Gl.Vertex3d(hw * 1.3, -hh * 1.3, z); Gl.Vertex3d(hw * 1.3, hh * 1.3, z); Gl.Vertex3d(-hw * 1.3, hh * 1.3, z); Gl.End();
            Gl.LineWidth(1.5f);
            Gl.Color4d(0.6, 0.6, 0.65, 0.8);
            Gl.Begin(Gl.LINES);
            for (int i = 1; i < 8; i++) { double x = -hw + 2 * hw * i / 8; Gl.Vertex3d(x, -hh, z); Gl.Vertex3d(x, hh, z); }
            for (int i = 1; i < 6; i++) { double y = -hh + 2 * hh * i / 6; Gl.Vertex3d(-hw, y, z); Gl.Vertex3d(hw, y, z); }
            Gl.End();
            Gl.LineWidth(3f);
            Gl.Color4d(1, 1, 1, 1);
            Gl.Begin(Gl.LINES);
            Gl.Vertex3d(-hw, -hh, z); Gl.Vertex3d(hw, -hh, z); Gl.Vertex3d(hw, -hh, z); Gl.Vertex3d(hw, hh, z);
            Gl.Vertex3d(hw, hh, z); Gl.Vertex3d(-hw, hh, z); Gl.Vertex3d(-hw, hh, z); Gl.Vertex3d(-hw, -hh, z);
            Gl.End();
            if (eye == 0) Gl.Color4d(1, 0.25, 0.25, 1); else Gl.Color4d(0.25, 1, 0.35, 1);
            Gl.LineWidth(4f);
            Gl.Begin(Gl.LINES);
            Gl.Vertex3d(-hw, 0, z); Gl.Vertex3d(hw, 0, z);
            Gl.Vertex3d(0, -hh, z); Gl.Vertex3d(0, hh, z);
            Gl.End();
            Gl.LineWidth(1f);
            Gl.Disable(Gl.BLEND);
            Gl.Color4d(1, 1, 1, 1);
        }

        /// <summary>
        /// "Floating window" edge matching: at the main screen's depth each eye sees a strip at one side that the
        /// other eye doesn't (the eyes are apart), so the edges look flat/double. Fade that strip out, plus a soft
        /// fade on every side edge, so both eyes' images end at the same place, at the screens' depth.
        /// </summary>
        void DrawEdgeMatch(int eye, int EW, int H, double tanX)
        {
            double fpx = EW / 2.0 / tanX;
            double disp = m_Settings.IpdMm / 1000.0 * fpx * (1 / MainDist() - m_Settings.StereoConverge);   // pixels, + = crossed
            double strip = Math.Min(Math.Abs(disp), EW / 4.0), fade = 48;
            // disp > 0: the left eye alone sees the leftmost strip, the right eye alone the rightmost (and vice versa)
            bool leftSide = (disp > 0) == (eye == 0);
            double wl = leftSide ? strip : 0, wr = leftSide ? 0 : strip;
            Gl.MatrixMode(Gl.PROJECTION); Gl.PushMatrix(); Gl.LoadIdentity(); Gl.Ortho(0, EW, H, 0, -1, 1);
            Gl.MatrixMode(Gl.MODELVIEW); Gl.PushMatrix(); Gl.LoadIdentity();
            Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.TEXTURE_2D);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            Gl.Begin(Gl.QUADS);
            // left edge: solid strip, then fade to clear
            Gl.Color4d(0, 0, 0, 1); Gl.Vertex2d(0, 0); Gl.Vertex2d(wl, 0); Gl.Vertex2d(wl, H); Gl.Vertex2d(0, H);
            Gl.Color4d(0, 0, 0, 1); Gl.Vertex2d(wl, 0); Gl.Color4d(0, 0, 0, 0); Gl.Vertex2d(wl + fade, 0); Gl.Vertex2d(wl + fade, H); Gl.Color4d(0, 0, 0, 1); Gl.Vertex2d(wl, H);
            // right edge
            Gl.Color4d(0, 0, 0, 1); Gl.Vertex2d(EW - wr, 0); Gl.Vertex2d(EW, 0); Gl.Vertex2d(EW, H); Gl.Vertex2d(EW - wr, H);
            Gl.Color4d(0, 0, 0, 0); Gl.Vertex2d(EW - wr - fade, 0); Gl.Color4d(0, 0, 0, 1); Gl.Vertex2d(EW - wr, 0); Gl.Vertex2d(EW - wr, H); Gl.Color4d(0, 0, 0, 0); Gl.Vertex2d(EW - wr - fade, H);
            Gl.End();
            Gl.Disable(Gl.BLEND);
            Gl.Color4d(1, 1, 1, 1);
            Gl.MatrixMode(Gl.PROJECTION); Gl.PopMatrix(); Gl.MatrixMode(Gl.MODELVIEW); Gl.PopMatrix();
        }

        void DrawText(string msg, double x, double y, uint fontBase)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(msg);
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(bytes[i] < 32 || bytes[i] > 127 ? 0 : bytes[i] - 32);
            Gl.RasterPos2d(x, y); Gl.ListBase(fontBase); Gl.CallLists(bytes.Length, Gl.UNSIGNED_BYTE, bytes);
        }

        void DrawCalibrationHelp(int W, int H, uint fontBase)
        {
            var s = m_Settings;
            double conv = s.StereoConverge;
            string[] lines =
            {
                "3D CALIBRATION   (red line = left eye, green line = right eye)",
                string.Format(CultureInfo.InvariantCulture, "Up / Down      merge red + green vertically      now {0:+0;-0;0} px", s.StereoVOffset),
                string.Format(CultureInfo.InvariantCulture, "Left / Right   depth where the eyes line up       now {0}", conv > 0.001 ? (1 / conv).ToString("F2", CultureInfo.InvariantCulture) + " m" : "far away"),
                string.Format(CultureInfo.InvariantCulture, "Space          line up at the main screen ({0:F2} m)", MainDist()),
                string.Format(CultureInfo.InvariantCulture, "[  ]           eye distance                       now {0:F0} mm", s.IpdMm),
                "E  edge matching " + (s.StereoEdgeMatch ? "ON " : "OFF") + "     R  reset     Enter  save     Esc  cancel",
            };
            Gl.MatrixMode(Gl.PROJECTION); Gl.PushMatrix(); Gl.LoadIdentity(); Gl.Ortho(0, W, H, 0, -1, 1);
            Gl.MatrixMode(Gl.MODELVIEW); Gl.PushMatrix(); Gl.LoadIdentity();
            Gl.Disable(Gl.TEXTURE_2D); Gl.Disable(Gl.DEPTH_TEST);
            int maxLen = 0; foreach (var l in lines) maxLen = Math.Max(maxLen, l.Length);
            double x = W / 2 - maxLen * 7, y0 = H * 0.16, lh = 36;
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            Gl.Color4d(0.08, 0.08, 0.1, 0.9);
            Gl.Begin(Gl.QUADS); Gl.Vertex2d(x - 24, y0 - 34); Gl.Vertex2d(x + maxLen * 14 + 24, y0 - 34); Gl.Vertex2d(x + maxLen * 14 + 24, y0 + lh * (lines.Length - 1) + 16); Gl.Vertex2d(x - 24, y0 + lh * (lines.Length - 1) + 16); Gl.End();
            Gl.Disable(Gl.BLEND);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0) Gl.Color4d(0.55, 0.8, 1, 1); else Gl.Color4d(1, 1, 1, 1);
                DrawText(lines[i], x, y0 + i * lh, fontBase);
            }
            Gl.Color4d(1, 1, 1, 1);
            Gl.MatrixMode(Gl.PROJECTION); Gl.PopMatrix(); Gl.MatrixMode(Gl.MODELVIEW); Gl.PopMatrix();
        }

        void DrawHud(int W, int H, uint fontBase)
        {
            if (m_Calibrating3D) DrawCalibrationHelp(W, H, fontBase);
            string msg = null;
            if (!m_Imu.Calibrated) msg = m_Imu.Status;
            else if (m_Toast != null && DateTime.Now < m_ToastUntil) msg = m_Toast;
            if (msg == null) return;
            Gl.MatrixMode(Gl.PROJECTION); Gl.PushMatrix(); Gl.LoadIdentity(); Gl.Ortho(0, W, H, 0, -1, 1);
            Gl.MatrixMode(Gl.MODELVIEW); Gl.PushMatrix(); Gl.LoadIdentity();
            var bytes = System.Text.Encoding.ASCII.GetBytes(msg);
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(bytes[i] < 32 || bytes[i] > 127 ? 0 : bytes[i] - 32);
            double x = W / 2 - msg.Length * 7, y = H * 0.82;
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            Gl.Color4d(0.08, 0.08, 0.1, 0.85);
            Gl.Begin(Gl.QUADS); Gl.Vertex2d(x - 24, y - 34); Gl.Vertex2d(x + msg.Length * 14 + 24, y - 34); Gl.Vertex2d(x + msg.Length * 14 + 24, y + 14); Gl.Vertex2d(x - 24, y + 14); Gl.End();
            Gl.Disable(Gl.BLEND);
            Gl.Color4d(1, 1, 1, 1);
            Gl.RasterPos2d(x, y); Gl.ListBase(fontBase); Gl.CallLists(bytes.Length, Gl.UNSIGNED_BYTE, bytes);
            Gl.MatrixMode(Gl.PROJECTION); Gl.PopMatrix(); Gl.MatrixMode(Gl.MODELVIEW); Gl.PopMatrix();
        }

        void LogStats()
        {
            double fx, fy, fz; m_Rel.Rotate(0, 0, -1, out fx, out fy, out fz);
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "late frames {0} worst {1:F0} ms upload avg {2:F2} ms draw {3:F2} ms swap {4:F2} ms | ", m_LateFrames, m_WorstFrameMs,
                m_UploadCount > 0 ? m_UploadMs / m_UploadCount : 0, m_UploadCount > 0 ? m_DrawMs / m_UploadCount : 0, m_UploadCount > 0 ? m_SwapMs / m_UploadCount : 0);
            m_LateFrames = 0; m_WorstFrameMs = 0; m_UploadMs = 0; m_UploadCount = 0; m_DrawMs = 0; m_SwapMs = 0;
            sb.AppendFormat("cadence 1x:{0} 2x:{1} 3x:{2} 4+:{3} | ", m_Cadence[1], m_Cadence[2], m_Cadence[3], m_Cadence[4]);
            Array.Clear(m_Cadence, 0, m_Cadence.Length);
            sb.AppendFormat(CultureInfo.InvariantCulture, "drift corr {0:F1} deg | temp {1:F1}C bias {2:F3},{3:F3},{4:F3} ({5} still blocks) | ",
                m_DriftTotal, m_Imu.TempC, m_Imu.BiasX, m_Imu.BiasY, m_Imu.BiasZ, m_Imu.BiasBlocks);
            sb.AppendFormat(CultureInfo.InvariantCulture, "render {0:F0} fps | imu {1:F0} Hz {2} | head yaw {3:F1} pitch {4:F1} | cursor {5:F1},{6:F1} over {7} | mode {8}",
                RenderFps, m_Imu.SampleRate, m_Imu.Status, Math.Atan2(-fx, -fz) / Deg, Math.Asin(Clamp(fy, -1, 1)) / Deg, m_CurYaw, m_CurPitch,
                m_Hover.OnDock ? "dock:" + m_Hover.Dock : (m_Hover.S != null ? m_Hover.S.Title + "/" + m_Hover.Part : "-"), m_Mode);
            foreach (var s in Snapshot())
                sb.AppendFormat(CultureInfo.InvariantCulture, " | {0} y{1:F0} p{2:F0} d{3:F2} w{4:F2}{5} {6:F0}fps dropped {8}/{9} [{7}]", s.Title, s.Yaw, s.Pitch, s.Dist, s.Width, s.Minimized ? " min" : "", s.Feed != null ? s.Feed.ActualFps : 0, s.Feed != null ? s.Feed.Method : "", s.Feed != null ? s.Feed.DupDropped : 0, s.Feed != null ? s.Feed.DupFrames : 0);
            Log.W(sb.ToString());
        }

        void SaveReadback(int W, int H)
        {
            try
            {
                using (var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                {
                    var bd = bmp.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                    ReadBuffer(0x0405); ReadPixels(0, 0, W, H, Gl.BGRA, Gl.UNSIGNED_BYTE, bd.Scan0);
                    bmp.UnlockBits(bd); bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    string path = Path.Combine(Settings.Dir, m_ReadbackName);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Log.W("Saved " + path);
                }
            }
            catch (Exception e) { Log.W("Readback failed: " + e.Message); }
        }

        // =====================================================================================================
        // PC-mode helpers (unchanged behaviour)
        // =====================================================================================================
        POINT m_LastOutside; bool m_HaveLastOutside;

        /// <summary>
        /// PC mouse mode: the glasses' own display is part of the Windows desktop but shows only the AR scene.
        /// When the mouse enters it, jump straight through to the screen on the far side (in the direction of
        /// travel); if there is none, push it back to the nearest screen.
        /// </summary>
        void KeepCursorOffGlasses()
        {
            var g = m_Glasses.Bounds;
            POINT p;
            if (!Native.GetCursorPos(out p)) return;
            bool inside = p.X >= g.Left && p.X < g.Right && p.Y >= g.Top && p.Y < g.Bottom;
            if (!inside) { m_LastOutside = p; m_HaveLastOutside = true; return; }
            if (m_HaveLastOutside)
            {
                int dx = p.X - m_LastOutside.X, dy = p.Y - m_LastOutside.Y;
                var q = Math.Abs(dx) >= Math.Abs(dy)
                    ? new POINT { X = dx > 0 ? g.Right : g.Left - 1, Y = p.Y }
                    : new POINT { X = p.X, Y = dy > 0 ? g.Bottom : g.Top - 1 };
                if (ScreenAt(q) != null) { Native.SetCursorPos(q.X, q.Y); m_LastOutside = q; return; }
            }
            MonitorEntry best = null; long bestD = long.MaxValue; int bx = 0, by = 0;
            foreach (var s in Snapshot())
            {
                var m = s.Mon; if (m == null) continue;
                int cx = Math.Max(m.Bounds.Left, Math.Min(m.Bounds.Right - 1, p.X)), cy = Math.Max(m.Bounds.Top, Math.Min(m.Bounds.Bottom - 1, p.Y));
                long d = (long)(cx - p.X) * (cx - p.X) + (long)(cy - p.Y) * (cy - p.Y);
                if (d < bestD) { bestD = d; best = m; bx = cx; by = cy; }
            }
            if (best != null) { Native.SetCursorPos(bx, by); m_LastOutside = new POINT { X = bx, Y = by }; }
        }

        long m_LastStrayLog = -100000; int m_Strays;
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);

        /// <summary>
        /// AR mouse mode: the real Windows pointer should only ever sit on a screen, under the AR cursor. If it
        /// gets onto the glasses' own display (the mouse hook missed some movement, e.g. while an administrator
        /// window had the focus, or Windows moved it after a display change) it would show as a second, small
        /// cursor in one eye: put it back where the AR cursor last put it.
        /// </summary>
        void KeepRealCursorHome()
        {
            if (m_Glasses == null) return;
            var g = m_Glasses.Bounds;
            POINT p;
            if (!Native.GetCursorPos(out p)) return;
            bool onGlasses = p.X >= g.Left && p.X < g.Right && p.Y >= g.Top && p.Y < g.Bottom;
            // (with "keep the mouse on the AR screens" it also never rests on a PC screen that isn't shown in AR)
            if (!onGlasses && !(m_Settings.ConfineMouse && ScreenAt(p) == null)) return;
            var q = m_LastSetCursor;
            if (ScreenAt(q) == null)
            {
                var main = MainScreen();
                if (main == null || main.Mon == null) return;
                var b = main.Mon.Bounds;
                q = new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 };
            }
            Native.SetCursorPos(q.X, q.Y);
            m_Strays++;
            if (m_Clock.ElapsedMilliseconds - m_LastStrayLog > 5000)
            {
                m_LastStrayLog = m_Clock.ElapsedMilliseconds;
                Log.W("Real cursor strayed onto " + (onGlasses ? "the glasses display" : "a screen that isn't shown in AR") + " ("+ p.X + "," + p.Y + "): put back (" + m_Strays + " so far, hook reinstalls " + m_Input.Reinstalls + ")");
            }
        }

        ScreenWin ScreenAt(POINT q)
        {
            foreach (var s in Snapshot())
            {
                if (s.Mon == null) continue;
                var b = s.Mon.Bounds;
                if (q.X >= b.Left && q.X < b.Right && q.Y >= b.Top && q.Y < b.Bottom) return s;
            }
            return null;
        }

        static bool Overlaps(RECT a, RECT b) { return a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom; }

        /// <summary>
        /// Makes the Windows desktop layout follow the AR layout: each virtual screen is placed beside the main
        /// screen on the side where it floats in AR (left/right/above/below), so the PC mouse (Ctrl+Alt+G) moves
        /// between screens the way they look. Only virtual screens move; the main screen and glasses stay put.
        /// </summary>
        void SyncWindowsLayout()
        {
            if (m_Busy) return;
            var screens = Snapshot();
            var main = MainScreen();
            if (main == null) return;
            var placed = new List<RECT> { main.Mon.Bounds, m_Glasses.Bounds };
            foreach (var m in DisplaySetup.ActiveMonitors()) if (!IsVirtual(m) && !m.IsGlasses) placed.Add(m.Bounds);   // PC screens stay put
            var virt = screens.FindAll(s => s.Virtual && s != main && s.Mon != null);
            virt.Sort((a, b) => (Math.Abs(AngleDiff(a.Yaw, main.Yaw)) + Math.Abs(a.Pitch - main.Pitch)).CompareTo(Math.Abs(AngleDiff(b.Yaw, main.Yaw)) + Math.Abs(b.Pitch - main.Pitch)));
            var moves = new List<KeyValuePair<string, POINT>>();
            var mb = main.Mon.Bounds;
            foreach (var s in virt)
            {
                double dyaw = AngleDiff(s.Yaw, main.Yaw), dp = s.Pitch - main.Pitch;   // +yaw = left in AR
                int w = s.Mon.Bounds.Width, h = s.Mon.Bounds.Height, x, y, sx = 0, sy = 0;
                if (Math.Abs(dp) > Math.Abs(dyaw) * 0.8) { x = mb.Left + (mb.Width - w) / 2; y = dp > 0 ? mb.Top - h : mb.Bottom; sy = dp > 0 ? -h : h; }
                else { y = mb.Top + (mb.Height - h) / 2; x = dyaw > 0 ? mb.Left - w : mb.Right; sx = dyaw > 0 ? -w : w; }
                var r = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
                for (int guard = 0; guard < 12 && placed.Exists(o => Overlaps(o, r)); guard++)
                { r.Left += sx; r.Right += sx; r.Top += sy; r.Bottom += sy; }
                placed.Add(r);
                if (r.Left != s.Mon.Bounds.Left || r.Top != s.Mon.Bounds.Top) moves.Add(new KeyValuePair<string, POINT>(s.Mon.DeviceName, new POINT { X = r.Left, Y = r.Top }));
            }
            if (moves.Count == 0) return;
            m_Busy = true;
            Task.Factory.StartNew(() =>
            {
                // one atomic CCD change that keeps the main screen at 0,0 (the legacy per-monitor calls could
                // hand the primary back to the PC screen)
                var want = new List<DisplayConfig.Want>();
                foreach (var m in DisplaySetup.ActiveMonitors())
                {
                    int mi = moves.FindIndex(mv => mv.Key == m.DeviceName);
                    want.Add(new DisplayConfig.Want { Device = m.DeviceName, X = mi >= 0 ? moves[mi].Value.X : m.Bounds.Left, Y = mi >= 0 ? moves[mi].Value.Y : m.Bounds.Top });
                }
                int rc = DisplayConfig.Apply(want, DisplayConfig.SaveArLayout);
                if (rc != 0)
                {
                    Log.W("Layout sync via CCD failed (" + rc + "); using the legacy API");
                    foreach (var mv in moves) VirtualScreens.SetPosition(mv.Key, mv.Value.X, mv.Value.Y);
                    VirtualScreens.Apply();
                }
                Thread.Sleep(600);
                if (m_MainDevice != null) DisplayConfig.EnsurePrimary(m_MainDevice, Log.W);
                Ui(() =>
                {
                    // Windows may shift displays to keep the desktop contiguous: follow the glasses if they moved.
                    FollowGlassesDisplay();
                    RefreshScreens(); m_Busy = false;
                    Log.W("Windows layout now follows the AR layout (" + moves.Count + " screen(s) moved)");
                });
            });
        }

        /// <summary>
        /// Windows on a display you can't see in AR (the glasses' own display, or the PC's screen when it isn't
        /// shown) are moved onto the main AR screen. 'all' = also maximized/any size windows (startup sweep).
        /// </summary>
        void RescueWindows(bool all)
        {
            var main = MainScreen();
            if (main == null || main.Mon == null) return;
            var hidden = new List<RECT> { m_Glasses.Bounds };
            var shown = Snapshot();
            foreach (var m in DisplaySetup.ActiveMonitors())
                if (!m.IsGlasses && !shown.Exists(s => s.Mon != null && s.Mon.DeviceName == m.DeviceName)) hidden.Add(m.Bounds);
            IntPtr self = Handle; int ownPid = Process.GetCurrentProcess().Id;
            var t = main.Mon.Bounds;
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (h == self || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == ownPid) return true;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                string cls = Native.ClassName(h);
                if (cls == "Progman" || cls == "WorkerW" || cls.StartsWith("Shell_") || cls == "Windows.UI.Core.CoreWindow") return true;
                if (string.IsNullOrEmpty(Native.WindowText(h))) return true;
                RECT r; if (!Native.GetWindowRect(h, out r) || r.Width <= 0) return true;
                int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
                if (!hidden.Exists(g => cx >= g.Left && cx < g.Right && cy >= g.Top && cy < g.Bottom)) return true;
                int w = Math.Min(r.Width, t.Width), hgt = Math.Min(r.Height, t.Height);
                bool max = Native.IsZoomed(h);
                if (max) Native.ShowWindow(h, Native.SW_RESTORE);
                SetWindowPos(h, IntPtr.Zero, t.Left + (t.Width - w) / 2, t.Top + (t.Height - hgt) / 2, w, hgt, 0x0004 | 0x0010);
                if (max) Native.ShowWindow(h, 3);
                Log.W("Moved window onto the main AR screen: " + Native.WindowText(h));
                return true;
            }, IntPtr.Zero);
        }

        // =====================================================================================================
        // self-test: add a screen, drag the main screen by its title bar with synthetic mouse input, arrange
        // =====================================================================================================
        void StartSelfTest()
        {
            double T = m_SelfTestSeconds;
            if (Program.CalTest)
            {
                // 3D calibration: open it, take readbacks before/after "line up at the main screen", cancel
                At(4, () => { Log.W("Self-test: 3D calibration"); StartCalibration3D(); });
                At(6, () => { m_ReadbackName = "selftest_cal3d_far.png"; m_ReadbackRequested = true; });
                At(7, () => { CalibrationKey(Hk.CalSnap); Log.W(string.Format(CultureInfo.InvariantCulture, "Self-test: lined up at {0:F2} m", 1 / m_Settings.StereoConverge)); });
                At(9, () => { m_ReadbackName = "selftest_cal3d_main.png"; m_ReadbackRequested = true; });
                At(10, () => CalibrationKey(Hk.CalCancel));
                At(T, () => Close());
                return;
            }
            if (Program.AiTest)
            {
                // Bubble AI end to end without a microphone: the question goes in as if transcribed
                At(5, () =>
                {
                    if (m_Crew == null) { Log.W("Self-test: Bubble AI didn't start"); return; }
                    Log.W("Self-test: asking Bubble: " + Program.AiTestQuestion);
                    m_Crew.Main.Inbox.Add(Program.AiTestQuestion);
                });
                At(T - 6, () => { m_ReadbackName = "selftest_ai.png"; m_ReadbackRequested = true; });
                At(T, () => Close());
                return;
            }
            if (Program.BubbleTest)
            {
                // Bubble: put him beside the main screen, read back, aim at him and click, read back the pop
                // (chained: the UI thread can be busy at start-up, which would bunch up independent timers)
                At(4, () =>
                {
                    Log.W("Self-test: bubble"); m_BubbleTestPlace = true;
                    At(1.5, () =>
                    {
                        m_ReadbackName = "selftest_bubble.png"; m_ReadbackRequested = true;
                        At(1, () =>
                        {
                            m_CurYaw = 14; m_CurPitch = 5;   // where ShowAt put him
                            m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Move });
                            m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Button, Button = 0, Down = true });
                            m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Button, Button = 0, Down = false });
                            At(0.12, () => { m_ReadbackName = "selftest_bubble_pop.png"; m_ReadbackRequested = true; });
                            At(1, () => Log.W("Self-test: bubble popped " + m_Bubble.Popped + " time(s)"));
                        });
                    });
                });
                At(T, () => Close());
                return;
            }
            if (Program.VideoLoad)
            {
                // judder test: animate the main screen like a video and just measure frame timing
                At(4, () => { var m = MainScreen(); if (m != null) { var v = new VideoLoad(m.Mon.Bounds); v.Show(); Log.W("Self-test: video load started"); At(T - 5, () => v.Close()); } });
                At(T - 7, () => { m_ReadbackName = "selftest_video.png"; m_ReadbackRequested = true; });
                At(T, () => Close());
                return;
            }
            At(T * 0.25, () => { Log.W("Self-test: add screen"); AddScreen(); });
            At(T * 0.45, () =>
            {
                var s = MainScreen();
                if (s == null) return;
                // aim the cursor at the middle of the title bar (local x = 0, y = h/2 + gap + T/2)
                double ly = s.Height / 2 + s.Gap + s.TitleH / 2;
                double px, py, pz; s.Pose.Rotate(0, ly, -s.Dist, out px, out py, out pz);
                double len = Math.Sqrt(px * px + py * py + pz * pz);
                m_CurYaw = Math.Atan2(-px / len, -pz / len) / Deg; m_CurPitch = Math.Asin(py / len) / Deg;
                Log.W(string.Format(CultureInfo.InvariantCulture, "Self-test: drag '{0}' from yaw {1:F1}", s.Title, s.Yaw));
                m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Move, Dx = 0, Dy = 0 });
                m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Button, Button = 0, Down = true });
                for (int i = 0; i < 10; i++) m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Move, Dx = 30, Dy = 0 });
                m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Button, Button = 0, Down = false });
                At(0.5, () => Log.W(string.Format(CultureInfo.InvariantCulture, "Self-test: '{0}' now at yaw {1:F1}", s.Title, s.Yaw)));
            });
            At(T * 0.6, () => { Log.W("Self-test: arrange"); m_ArrangeRequested = true; });
            At(T * 0.68, () =>
            {
                // point at the centre of the right-most screen's content and check the real mouse lands there
                var list = Snapshot().FindAll(x => !x.Minimized);
                if (list.Count == 0) return;
                list.Sort((a, b) => a.Yaw.CompareTo(b.Yaw));
                var s = list[0];
                double px, py, pz; s.Pose.Rotate(0, 0, -s.Dist, out px, out py, out pz);
                double len = Math.Sqrt(px * px + py * py + pz * pz);
                m_CurYaw = Math.Atan2(-px / len, -pz / len) / Deg; m_CurPitch = Math.Asin(py / len) / Deg;
                m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Move, Dx = 1, Dy = 0 });
                At(0.4, () =>
                {
                    POINT c; Native.GetCursorPos(out c); var b = s.Mon.Bounds;
                    Log.W(string.Format("Self-test: cursor over '{0}' content -> real mouse at {1},{2}; that screen spans {3},{4} {5}x{6} (centre {7},{8})",
                        s.Title, c.X, c.Y, b.Left, b.Top, b.Width, b.Height, b.Left + b.Width / 2, b.Top + b.Height / 2));
                });
            });
            At(T * 0.75, () => { m_ReadbackRequested = true; });
            At(T * 0.8, () =>
            {
                if (!Program.FixedHead) return;
                Quat dq; double ddist, dcy, h; DockFrame(out dq, out ddist, out dcy, out h);
                double px, py, pz; dq.Rotate(0, dcy, -ddist, out px, out py, out pz);
                double len = Math.Sqrt(px * px + py * py + pz * pz);
                Program.FixedPitch = Math.Asin(py / len) / Deg;   // look at the dock (yaw stays 0)
                m_CurYaw = Math.Atan2(-px / len, -pz / len) / Deg; m_CurPitch = Program.FixedPitch;
                m_Input.Events.Enqueue(new InputEvt { Kind = InputKind.Move });
            });
            At(T * 0.9, () => { if (Program.FixedHead) { m_ReadbackName = "selftest_dock.png"; m_ReadbackRequested = true; } });
            At(T, () => Close());
        }

        /// <summary>A full-screen 60 fps animation on the main AR screen, standing in for a video.</summary>
        sealed class VideoLoad : Form
        {
            int m_F; readonly System.Windows.Forms.Timer m_T = new System.Windows.Forms.Timer { Interval = Environment.GetEnvironmentVariable("XR_VIDEO_30") != null ? 33 : 15 };
            public VideoLoad(RECT b)
            {
                FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; ShowInTaskbar = false; TopMost = true;
                Bounds = new Rectangle(b.Left, b.Top, b.Width, b.Height); DoubleBuffered = true;
                m_T.Tick += delegate { m_F++; Invalidate(); }; m_T.Start();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; int w = ClientSize.Width, h = ClientSize.Height;
                for (int i = 0; i < 16; i++)
                    using (var br = new SolidBrush(Color.FromArgb(255, (i * 37 + m_F * 3) % 256, (i * 91 + m_F * 5) % 256, (i * 53 + m_F * 7) % 256)))
                        g.FillRectangle(br, ((i * w / 16) + m_F * 12) % w, 0, w / 16 + 2, h);
                using (var f = new Font("Segoe UI", 64)) g.DrawString("Frame " + m_F, f, Brushes.White, w / 3, h / 3);
            }
            protected override void Dispose(bool d) { m_T.Dispose(); base.Dispose(d); }
        }

        void At(double seconds, Action a)
        {
            var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)(seconds * 1000)) };
            t.Tick += delegate { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }
    }
}
