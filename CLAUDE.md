# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

AirBubble: a Windows app that shows PC screens as floating AR windows in XREAL Air glasses, plus "Bubble", a
voice assistant (Claude + ElevenLabs). Plain C# on .NET Framework 4.8, WinForms + raw P/Invoke (OpenGL, DXGI,
HID, CCD display API). No project files, no NuGet, no third-party libraries.

`README.md` ("For developers") has the per-area implementation table, the full diagnostics command line, known
issues and the file-by-file source layout. Read it before changing an area. On `dev` it lags the code in two
places: the glasses now run at 120 Hz in 2D (`Settings.RefreshHz`), and the desktop arrangement differs (see
"Local changes" below).

## Build

Everything is compiled by batch files calling the `csc.exe` that ships with Windows
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`). From PowerShell, call them through `cmd.exe`:

```powershell
& cmd.exe /d /c "Viewer\build.bat"      # -> Viewer\bin\AirBubble.exe
& cmd.exe /d /c "Installer\build.bat"   # viewer + driver + installer -> dist\AirBubbleSetup.exe
& cmd.exe /d /c "ImuTest\build.bat"     # -> ImuTest\ImuTest.exe (sensor recorder)
```

- **C# 5 only.** That compiler predates Roslyn: no `$"..."`, `?.`, `nameof`, `out var`, expression-bodied
  members, pattern matching or tuples. Use `string.Format`, explicit null checks, declared `out` variables.
- `Viewer\build.bat` compiles `src\*.cs shared\*.cs` by wildcard, so a new file needs no registration. A new
  assembly reference needs a `/r:` in the batch file.
- Files cross the project boundary: the viewer compiles `Installer\VddInstall.cs`; the installer compiles
  `Viewer\src\UacPrompts.cs` and embeds the built viewer, `USER_GUIDE.txt` and `Installer\vdd\*` as resources.
- The last build step signs with a self-signed certificate (`Installer\signing\sign.ps1`). If the certificate
  is missing or signing fails, the exe is still built, just unsigned.
- A running `AirBubble.exe` locks its file: `AirBubble.exe --quit`, wait for the non-`--guard` process to
  exit, then build. The installed copy and `Viewer\bin` are separate files.
- Release bump: `Viewer\src\AssemblyInfo.cs`, plus `Product.Version` and the assembly attributes in
  `Installer\Setup.cs`.
- Source files are CRLF (`core.autocrlf=true`). Git Bash `sed -i` rewrites them as LF; use the Edit tool.

## Checking a change

There is no test project and no linter. Checks are command-line modes of the exe itself (all in
`Program.Main`, `Viewer\src\ViewerApp.cs`). The exe is a `winexe` that attaches to the parent console for
these modes; anything else that needs console output needs a `/target:exe` build.

Offline (no glasses, PC untouched):

```powershell
Viewer\bin\AirBubble.exe --stab-test ImuTest\guided.csv        # stabiliser: jitter when still, lag when turning
Viewer\bin\AirBubble.exe --wiggle-test ImuTest\wave.csv        # accelerometer-injected motion, prediction error
Viewer\bin\AirBubble.exe --replay ImuTest\drift.csv [--saved]  # recording through the real tracker, yaw per second
Viewer\bin\AirBubble.exe --autonomy-test samples.txt out.txt   # ActionGuard risk ratings + ask/go per mode
Viewer\bin\AirBubble.exe --bubble-ask "..." --autonomy 0|1|2 --app-control --dry-run   # real Claude call, PC untouched
```

`--replay` overwrites `%APPDATA%\AirBubble\imu_bias.ini` (device name "replay"): back it up first.

With glasses plugged in: `--selftest N [--fixed-head] [--video-load] [--3d] [--cal-test] [--bubble-test]
[--ai-test "question"]` runs the real viewer for N seconds, scripted (`StartSelfTest` in `ArDesktop.cs`), and
writes `selftest_*.png` readbacks. This and any normal start rearrange the live Windows displays and switch
the glasses' mode, so the picture in the glasses blanks for several seconds.

Runtime evidence is in `%APPDATA%\AirBubble\`: `viewer.log` (truncated on each start; a stats line every 30 s
with late frames, swap ms, render fps, IMU Hz), `guard.log`, `bubble_actions.log`, `settings.ini`,
`layout.ini`, `imu_bias.ini`, `keys.dat` (DPAPI).

## Architecture

### One exe, several roles

`Program.Main` is a long chain of `--flag` checks that each run and return, followed by the real viewer. The
roles that run as separate processes:

- **Viewer** (no flag / `--autostart`): holds mutex `XrealPcViewer.SingleInstance`; holds
  `XrealPcViewer.Showing` only while it owns the displays. `--quit` signals event `XrealPcViewer.Quit`.
- **Guard** (`--guard`, `Guard.cs`): started at logon, polls every 1.5 s, and puts the desktop back to a sane
  PC layout when the glasses are plugged/unplugged or the viewer died. It stands aside whenever the `Showing`
  mutex exists.
- **Elevated one-shots** (`--vdd-screens n`, `--uac-prompts on|off|restore`): the viewer relaunches itself
  with `runas` for the single step that needs admin, then reads the exit code.

The `XrealPcViewer.*` names (mutexes, events, DPAPI entropy, registry key, namespace `XrealDesktop.PcViewer`)
are the pre-1.7 product name and are kept on purpose for upgrade compatibility. Do not rename them.

### Viewer lifecycle

`Main` loops over `RunOnce`: set the glasses' display mode over HID (`XrealMcu`) → wait for Windows to see the
display again → `DisplaySetup.EnsureExtended` → start the IMU → `Application.Run(new ViewerForm(...))`. The
form's result (`Quit`, `Restart`, `Unplugged`, `Error`) decides whether the loop goes round again. `Cleanup`
always puts the glasses back to 2D 60 Hz and calls `DisplayConfig.RestorePcDesktop`. Every step that touches
display topology is followed by a fixed sleep or a poll, because Windows applies changes asynchronously;
removing those waits breaks things that only show on real hardware.

`ViewerForm.OnShown` then, on a background task, attaches the virtual screens to keep, applies one atomic
arrangement with `DisplayConfig.Apply` (a virtual screen becomes the Windows primary at 0,0 while the viewer
runs), and starts the render thread.

### Threads and how they share state

`ViewerForm` is one `partial class` split over `ArDesktop.cs` (1,800 lines: lifecycle, screens, interaction,
rendering, 3D calibration, self-test) and `ArBubbles.cs` (Bubble/crew drawing). Its fields are grouped by the
thread that owns them, and the comments say which.

- **UI thread**: WinForms message loop, global hotkeys (`WndProc`), tray menu, `UiTick` (15 ms timer),
  Settings window, adding/removing screens.
- **Render thread** (`RenderLoop`): owns the OpenGL context. Per frame: upload new capture frames to textures,
  wait until just before the refresh, sample the head pose as late as possible (`LatchMarginMs`), run the
  stabiliser and drift absorption, process AR-cursor input, draw, swap. All GL calls and all AR interaction
  state live here.
- **Capture threads**: one per `ScreenFeed` (DXGI Desktop Duplication, GDI fallback). Frames are queued with
  Windows' present timestamps and handed to the renderer by pointer swap (`TakePacedFrame` / `TakeFrame`).
- **IMU thread** (`XrealImu`): 1000 Hz HID reads into the Mahony filter; `GetPose(predictSeconds)` is called
  from the render thread.
- **Crew threads** (`AiCrew.cs`): one worker per agent (Bubble plus helper bubbles) running a Claude tool
  loop, plus `Ears` (offline wake word, then ElevenLabs transcript) and `Speaker`.

Conventions for crossing threads, used everywhere:

- The screen list is copy-on-write: mutate under `m_Lock` by replacing `m_Screens`; readers call `Snapshot()`.
  Removed screens go to `m_Dead` so the render thread frees their textures.
- UI → render: set a `volatile bool m_...Requested` flag that the render loop consumes.
- Render/other → UI: `Ui(action)` (`BeginInvoke`).
- `Settings` fields are `volatile` and read live by the render thread; change them, then `NotifyChanged()`
  (saves and raises `Changed`). A new setting needs the field, a `case` in `Load`, and a slot in the format
  string in `Save`.
- The render thread reads `CrewAgent` state only through its `volatile` fields; conversation state belongs to
  the agent's own thread.

### Bubble's safety layer

Every action Bubble takes on the PC (open, click, type, keys) goes through `AiSafety.cs`: `ActionGuard` gives
the app's own `Risk` rating, the higher of that and the model's self-rating counts, and `Autonomy.NeedsAsk`
decides ask-or-go for the current mode. `Critical` is asked in every mode. Actions are written to
`bubble_actions.log`. Keep new tools on this path, and extend `--autonomy-test` samples when the rating rules
change.

### Other things worth knowing

- `Viewer\shared\` is copied from another project ("XREAL Desktop host", namespace `XrealDesktop.Host`). Only
  `Native.cs` and parts of `Capture.cs` are used; `Protocol.cs` is carried along unused.
- The virtual display driver in `Installer\vdd\` is a prebuilt third-party binary (MIT). `VddInstall.cs`
  installs it and rewrites its config; showing or closing an AR screen attaches or detaches a monitor, it never
  reloads the driver.
- Saved screen positions in `layout.ini` are keyed by Windows device name (`virtual:\\.\DISPLAYn`), which
  changes when the driver restarts (README, Known issues).
- Only two commands are ever sent to the glasses' control interface: read and write display mode. Mode
  switches are kept `ModeGapSec` apart across runs (stamp file) because two close together left the glasses
  unresponsive until replugged.
- `ImuTest\*.csv` are real sensor recordings; the tracker and stabiliser were tuned against them, and the
  offline test modes above take them as input.
- `LidWatcher\` is a PowerShell workaround for an open bug (display topology changes while running are not
  handled); see its README.

## Local changes on `dev`

This fork carries changes marked `// Local change:` in the code (PC screen placed right of the main AR
screen and re-applied once if Windows ignores it, in `ArDesktop.cs`; Bubble and Bubble AI off by default, in
`Settings.cs`). Keep the marker on such changes so they stay easy to find when merging upstream.
