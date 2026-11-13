<div align="center">

<img src="docs/images/hero-banner.png" alt="SmartMacro Studio — AAA Desktop & Game Macro IDE for Windows" width="100%" />

<br />

[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20(x64)-0ea5e9?style=for-the-badge&logo=windows&logoColor=white)](https://github.com)
[![Runtime](https://img.shields.io/badge/.NET-10%20WPF-6366f1?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![Version](https://img.shields.io/badge/Release-v1.0.0-22d3ee?style=for-the-badge)](https://github.com)
[![Tests](https://img.shields.io/badge/Automated%20Tests-1%2C424%20Passing-10b981?style=for-the-badge)](tools/verify.cmd)
[![Privacy](https://img.shields.io/badge/Local--First-Zero%20Cloud-a855f7?style=for-the-badge)](#requirements--installation)

**All-round AAA desktop & game macro IDE for Windows — record, detect, branch, and debug workflows visually.**

Developer: **Riku (リク)** · Studio: **KuroLogic** · Built with `.NET 10` (WPF, `win-x64`)

[Visual Tour](#visual-tour) · [Key Capabilities](#key-capabilities) · [Architecture](#system-architecture) · [Keyboard Shortcuts](#keyboard-shortcuts) · [Build & Verify](#requirements--installation) · [Community](#community--support)

</div>

---

SmartMacro Studio is a **100% local-first** Windows automation IDE built for both **daily productivity ("Workbench")** and **high-FPS gaming ("Arena")**. Capture desktop and 3D raw mouse input on one timeline, convert recordings into structured visual blocks, locate occluded or moving targets with **Vision Pipeline 2.0**, stabilize fast minigames at **120 FPS** with a **Smart Predictive PD Servo**, track typed variables & **WASAPI** audio peaks, trigger macros via hotkeys, schedules, or window events, and step-debug every branch interactively — with **zero scripting required** and **zero cloud uploads**.

---

## Visual Tour

### 1. 3-Pane Workflow Editor, Vision 2.0 & Live Debugger

> Build complex automation visually with collapsible **`Group … End Group`** blocks (`Ctrl+Shift+G`), block-indented guides, a right-edge **Workflow Minimap**, **`F9` / `F10` / `F11`** breakpoints and stepping, **per-step profiler badges** (`×N · ms`), and **Live Brain Variables** docked at the bottom.

<p align="center">
  <img src="docs/images/studio-editor.png" alt="SmartMacro Studio 3-Pane Workflow Editor with collapsible groups, Find image crosshair preview, F9 breakpoint, per-step timing telemetry, and Live Brain Variables dock" width="100%" />
</p>

### 2. Macro Library, Workflow Fingerprints & 1-Click Health Audit

<table>
  <tr>
    <td width="50%" valign="top">
      <a href="docs/images/studio-library.png">
        <img src="docs/images/studio-library.png" alt="SmartMacro Macro Library with Workflow Fingerprint strips and Inspector" width="100%" />
      </a>
      <br />
      <strong>Obsidian Neon Macro Library & Inspector</strong>
      <br />
      Organize routines across <em>Arena</em> & <em>Workbench</em> profiles, pin favorites, visualize workflow composition via color-coded <strong>Workflow Fingerprint</strong> strips, and export standalone or sub-macro-bundled <code>.smacro</code> files.
    </td>
    <td width="50%" valign="top">
      <a href="docs/images/studio-audit.png">
        <img src="docs/images/studio-audit.png" alt="SmartMacro 1-Click Library Health and Portability Audit" width="100%" />
      </a>
      <br />
      <strong>1-Click Library Health & Portability Audit</strong>
      <br />
      Scan every macro in your library before running or sharing to catch broken sub-macro calls, cyclic graphs, hotkey collisions, missing vision targets, or machine-specific paths — with 1-click jump-to-step fixes.
    </td>
  </tr>
</table>

### 3. Floating Mission Control (`Ctrl+M`) & Live Telemetry HUD

<table>
  <tr>
    <td width="40%" valign="top">
      <a href="docs/images/mission-control-hud.png">
        <img src="docs/images/mission-control-hud.png" alt="SmartMacro Floating Mission Control Always-On-Top Telemetry HUD" width="100%" />
      </a>
    </td>
    <td width="60%" valign="middle">
      <h3>Stay in Command While Automated Runs Execute</h3>
      <ul>
        <li><strong>Always-On-Top Live Pod & Mini-Bar:</strong> Keep full telemetry visible or collapse into a sleek TinyTask-style Mini-Bar while your game or workspace runs underneath.</li>
        <li><strong>Real-Time Step & Vision Telemetry:</strong> Monitor current loop counts, elapsed clock, sub-millisecond vision timing, active step progress, and watcher status at 30 FPS UI throttling with zero playback overhead.</li>
        <li><strong>Live Brain Variable Watch:</strong> Track live counters, OCR numbers, and strings updating in real time — plus instant <code>Pause (F8)</code> and <code>Stop (Esc)</code> controls.</li>
      </ul>
    </td>
  </tr>
</table>

### 4. Studio Appearance, Context Profiles & Hardware Acceleration

> Customize **Obsidian Neon** or **Midnight Blue** themes, 5 neon accents, layout density, Beginner/Advanced inspector complexity, Windows startup (`--tray`), and automatic `.smbackup` library archives.

<p align="center">
  <img src="docs/images/studio-settings.png" alt="SmartMacro Settings view with KuroLogic Studio branding and appearance controls" width="100%" />
</p>

---

## System Architecture

```mermaid
flowchart TB
    subgraph Triggers["⚡ Reflex & Context (Triggers, Profiles & Tray)"]
        HK["Per-Macro Hotkeys\n(PressOnce · HoldToRun · RepeatWhileHeld · Toggle)"]
        SCHED["Schedules & Timers\n(Startup · Interval · Daily HH:mm)"]
        WIN["Window Events & Auto-Profiles\n(Window Appears/Focused · Foreground Profile Router)"]
        GOV["Run Governor\n(IgnoreIfBusy · QueueNext · PreemptCurrent)"]
        HK & SCHED & WIN --> GOV
    end

    subgraph Studio["🎛 Studio X Workflow IDE"]
        TB["Searchable Toolbox\n+ 10 1-Click Blueprints + Quick Patterns"]
        CANVAS["Collapsible Block Canvas\n(Groups · Sub-Macros · Minimap · F9 Breakpoints)"]
        DBG["Interactive Debugger & HUD\n(F9 Break · F10 Over · F11 Into · Shift+F11 Out · Ctrl+M HUD)"]
        DOCK["Bottom Dock\n(Activity · Problems · Live Variables · Watchers · Triggers · Debug · Trace)"]
    end

    subgraph Engine["🚀 Playback & Vision 2.0 Engine (SmartMacro.Core)"]
        INTENT["🧭 5-Tier Smart Intent Relocator\n(UIA → OCR → Multi-Scale Image → Window → Coords)"]
        VISION["👁 Vision Pipeline 2.0 & HUD Probes\n(Partial Occlusion · Edge/Shape · 2D Kalman Tracker · FrameBus Motion ROI)"]
        TEXTCOLOR["🔤 Text 2.0 & 🎨 Color 2.0\n(O(1) 1,000-Word OCR + 60 FPS Track · CIE Lab/HSV + Blob Circularity · Gauge 0-100%)"]
        BRAIN["🧠 Brain Runtime\n(Typed Variables · Text/CSV Ops · Checkpoints · Watchers · Call-Frame Sub-Macros)"]
        SERVO["⚖️ Smart Predictive PD Servo\n(120 FPS Minigame Bar Tracking + Stopping-Distance Brake + PWM)"]
    end

    GOV --> Engine
    Studio <--> Engine
```

---

## Key Capabilities

### 1. 👁 Vision Pipeline 2.0: Occluded Targets, Moving Targets, Text 2.0 & Color 2.0
- **Partial-Visibility (Occlusion-Tolerant) Image Matching:** Splits templates into a `3×3` or `4×4` sub-patch grid and scores unoccluded cells (`MinVisiblePercent`), reliably matching targets even when 30–40% covered by particles, hit flashes, or floating combat text.
- **Edge / Shape Gradient Detector & Rotation Tolerance:** Sobel gradient-orientation contour matching (`EdgeShapeMatcher`) stays invariant under severe lighting or color shifts, with optional `±0°–45°` rotation tolerance (`RotationRangeDeg`).
- **2D Kalman / Alpha-Beta Target Tracker & Predictive Lead Clicks:** Tracks moving targets across frames (`TargetTracker`), estimates 2D velocity `(VelocityX, VelocityY)` in `px/s`, expands the ROI along trajectory, and leads clicks ahead of moving targets (`PredictiveClick`, `PredictiveLeadMs`).
- **Temporal N-Frame Confirmation & Motion-Gated ROI (`FrameBus`):** `ConfirmFrames` (`2..10`) rejects 1-frame particle flashes; `OnlyWhereMoved` uses a thread-safe `16×16` grid motion mask to scan only pixels that changed, alongside `< 1 ms` Hot-Spot ROI caching.
- **🔤 Text 2.0 (`O(1)` 1,000-Word Index & Find-Once-Then-Track):**
  - Instant `O(1)` hash-table lookup across 1,000+ words on screen, 64-bit character bitmask pre-filtering before fuzzy Levenshtein matching, and regex matching (`UseRegexPattern`).
  - **Find-Once-Then-Track (`TrackTextAsImage`):** Once OCR locates a target word, SmartMacro crops its live bounding box and tracks it visually at 60 FPS (`~1–3 ms`) without re-running OCR every frame.
  - Built-in **OCR Image Preprocessor** (`Auto`, `HighContrast`, `InvertLightOnDark`, `BinarizeOtsu`).
- **🎨 Color 2.0 (Perceptual CIE Lab / HSV & Blob Shape Filtering):** Match colors in `LabDeltaE` ($\Delta E$), `Hsv`, or `Rgb`, filter blobs by circularity (`MinCircularityPercent`), fill ratio, and pixel area, and select by `Largest`, `NearestToCenter`, `NearestToLastSpot`, or `TopMost`.
- **✨ 1-Click Scenario Presets, Shared Templates & Live Auto-Tune:** Pick scenario cards (`Static UI`, `Game HUD`, `Moving Target`, `Busy Particles`, `Scrolling Text`, `Fast Reaction`), reuse crops across steps via the **Shared Template Gallery**, or run **Auto-Tune** (`TemplateAutoTuner`) to calibrate optimal thresholds automatically.

### 2. ⚖️ Smart Predictive Feedback Loop 2.0 (120 FPS Minigame Servo)
- **Adaptive PD Controller + Stopping-Distance Brake:** Tracks moving targets and catch zones up to **120 FPS** (`FeedbackControlMode.SmartPredictive`), predicting coasting distance from live velocity and braking before overshoot.
- **Hover Pulse PWM & 4 Behavior Cards:** Choose 1-click tuning presets (**🧲 Momentum Bar**, **⬇️ Gravity Bar**, **⚡ Snap Bar**, **🍃 Floaty Bar**) or tune lead prediction, deadzone, and PWM duty cycle for horizontal or vertical gauges.

### 3. 🧭 Dual-Stream Recording & 5-Tier Smart Intent Relocator
- **One Record Button for Desktop Apps & 3D Games:** Captures desktop coordinates and raw relative mouse deltas (`WM_INPUT`) on one timeline. Cursor segments become editable **Move / Click / Key / Delay** steps; locked-camera turns become **Recorded input** steps with stuck-key guards and smooth catch-up debt limiting.
- **5-Tier Resilient Target Relocator:** When **Intent Recording** is enabled, recorded clicks capture an off-thread `TargetFingerprint` and relocate automatically when windows move or scale:
  1. **UI Automation element** (`AutomationId`, `Name`, `ControlType`, normalized click point — with single-flight protection against frozen UI threads)
  2. **Nearby OCR text anchor** + pixel offset
  3. **56×56 Image Snapshot** (1:1 fast path + **75%–150% multi-scale pyramid** for DPI/zoom shifts)
  4. **Window-Relative coordinate anchor** (window title + process name)
  5. **Recorded coordinates fallback**
- **🔧 Opt-In Smart Self-Heal & Adaptive Delays:** When playback relocates a drifted click by `≥ 25 px`, a 1-click editor banner offers to update coordinates (`Ctrl+Z` undoes it). **Delay** steps can wait fixed intervals, **✨ Until screen settles**, or **⚡ Until screen changes**, alongside optional **Humanize** jitter and curved glides.

### 4. 🧠 Brain 2.0: Variables, WASAPI Audio Meter, Checkpoints, Watchers & Sub-Macros
- **Typed Variables, Math & `{placeholders}`:** Declare `Number`, `Text`, or `Boolean` variables with live runtime inspection. Supports `Set`, `Add`, `Subtract`, `Multiply` (`×=`), `Divide` (`÷=`), `Modulo` (`%=`), `Round`, text cleanup (`Trim`, `ExtractDigits`, `UPPERCASE`, `lowercase`), clipboard export, and `{varName}` substitution across steps.
- **Live Screen, HUD Gauge, System Audio & CSV Data Sources:** Populate variables from constants, **OCR numbers/text**, **📊 Bar / gauge fill % (`0–100%`)** across `1080p` / `1440p` / `4K`, **🔊 System master audio peak % (`0–100%`)** via zero-dependency WASAPI, **🎲 Random integer**, **🖥 Active window title**, **🕒 Date/time**, **Clipboard**, **Last found X/Y**, **Loop counter**, **Elapsed seconds**, or **📄 N-th line / CSV item**.
- **➕ Compound Logic (`AND` / `OR`) & Workbench Window Snap:** Combine multiple `Text`, `Image`, `Color`, or `Variable` conditions inside `If` / `Repeat` blocks, and snap windows (`SnapLeftHalf`, `SnapRightHalf`, `Maximize`, `CenterOnPrimary`) via **Focus window**.
- **📍 Checkpoints, Watchers & Modular Sub-Macros:** Configure per-step failure recovery (**Continue**, **Skip next step**, **Stop**, **Retry**, **Run recovery sub-macro**, or **Jump to Checkpoint**), guard runs with periodic **Background Watchers**, **Extract** repeated step blocks into reusable sub-macros (`CallMacroStep`) with isolated call frames & cycle checks, and share everything in self-contained `.smacro` bundles.

### 5. ⚡ Reflex & Context: Triggers, Run Governor, Auto-Profiles & System Tray
- **Per-Macro Triggers:** Attach **Hotkeys** (**Press once**, **Hold to run**, **Repeat while held**, **Toggle**), **Schedules** (**On startup**, **Every N minutes/seconds**, **Daily at `HH:mm`**), or **🖥 Window Events** (**Window opened / focused**) directly to any macro.
- **Run Governor, Auto-Profiles & Background Startup:** Configure conflict behavior (**Ignore if busy**, **Queue next**, **Preempt & stop current**), toggle **⚡ Triggers Armed**, enable **Auto-switch active profile from foreground window**, minimize to the **System Tray**, and launch silently with Windows (`--tray`).

### 6. 🎛 Studio X & Evergreen Reliability: Groups, Debugger, Blackbox & Backups
- **Organized Block Workflows:** Collapse noisy sequences into color-tagged **`Group … End Group`** sections (`Ctrl+Shift+G`), preview structure on the **Workflow Minimap**, jump with `Ctrl+K` **Command Palette**, and clean recordings in 1 click (**Clean up** / **Merge keys**).
- **🔴 Full Call-Frame Debugger (`F9` / `F10` / `F11` / `Shift+F11`) & Vision Test (`Ctrl+T`):** Toggle `F9` breakpoints, step over (`F10`), step *into* called sub-macros (`F11`) with active runtime call-frame variables, step *out* (`Shift+F11`), spot slow steps via per-step timing badges (`×N · X ms`), and test any step live (`Ctrl+T`).
- **🔍 Blackbox Flight Recorder, Sound Cues & Webhooks:** Stopping on a vision timeout automatically saves `last-failure-snapshot.png` for 1-click inspection. **Take screenshot** steps support synthesized WAV audio cues, formatted logs, and Discord/Slack HTTPS webhooks.
- **📦 Self-Service Library Recovery & Diagnostics:** Audit all macros (`Audit Library`), recover deleted macros from **🗑 Trash**, create 1-click `.smbackup` Studio archives, copy safe diagnostics, and rely on automatic 30-day log/trash pruning.

---

## Keyboard Shortcuts

| Shortcut | Action | Scope |
| :--- | :--- | :--- |
| `Ctrl+Shift+F9` *(customizable)* | Start / stop recording | Global (anywhere) |
| `Ctrl+Shift+F10` *(customizable)* | Play macro / stop playback | Global (anywhere) |
| `F8` *(customizable)* | Pause / resume playback (releases held keys while paused) | Global while playing |
| `Esc` | Emergency stop (releases all held keys and buttons immediately) | Global while playing |
| `F9` | Toggle debugger breakpoint on selected step | Editor |
| `F10` | **Step Over:** run one step while paused and pause before the next | Editor / While paused |
| `F11` | **Step Into:** step into a called sub-macro (`CallMacroStep`) and inspect its call frame | Editor / While paused |
| `Shift+F11` | **Step Out:** finish the current sub-macro frame and pause back at the caller | Editor / While paused |
| `Ctrl+T` | **Test Step:** test vision detection once (with near-miss score) or run action step once | Editor |
| `Ctrl+F5` | Play only the selected step (or whole `If` / `Repeat` / `Group` block) | Editor |
| `Shift+F5` | Play from the selected step to the end | Editor |
| `Ctrl+K` | Open **Command Palette** (add steps, blueprints, play, auto-fix, speed…) | Studio |
| `Ctrl+M` | Toggle floating **Mission Control** live telemetry HUD window | Studio |
| `Ctrl+F` | Search steps in workflow (`F3` / `Shift+F3` next/previous match) | Editor |
| `Ctrl+S` | Save workflow | Editor |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo (`Ctrl+Shift+Z` also redoes) | Editor |
| `Ctrl+C` / `Ctrl+V` / `Ctrl+D` | Block-aware Copy / Paste / Duplicate | Step list |
| `Space` | Enable / disable selected steps (or whole block) | Step list |
| `←` / `→` | Fold / unfold selected `If`, `Repeat`, or `Group` block | Step list |
| `Alt+↑` / `Alt+↓` | Block-aware move step up / down (`Alt+Home` / `Alt+End` for top / bottom) | Step list |
| `Ctrl+Shift+I` / `Ctrl+Shift+R` | Wrap selected steps in `If … End If` / `Repeat … End Repeat` | Step list |
| `Ctrl+Shift+G` | Wrap selected steps in `Group … End Group` | Step list |

---

## Requirements & Installation

- **OS:** Windows 10 version 2004 (May 2020 Update, build 19041) or newer, or Windows 11 (`x64`).
- **OCR:** Uses the built-in Windows OCR engine (`Windows.Media.Ocr`). Check **Settings → Text recognition** in SmartMacro to verify your display language's OCR pack is installed.
- **Data Location & Portable Mode:** By default, macros, settings, logs, and crash diagnostics are stored locally in `%LOCALAPPDATA%\SmartMacro` (never inside the application folder, so updating preserves your library). To run in **Portable Mode** (for a USB drive or synced folder), create an empty `portable.txt` file or a `PortableData` folder next to `SmartMacro.exe`.

### Build, Verify & Package from Source

Requires the **.NET 10 SDK** on Windows:

```powershell
# Build with 0 warnings / 0 errors and run all 1,424 automated tests (logs written to artifacts\build.log & artifacts\test.log)
cmd /c tools\verify.cmd

# Run tests, publish a self-contained single-file win-x64 EXE, verify third-party notices, and create the release ZIP
cmd /c tools\publish.cmd
```

---

## Safety & Honest Limits

- **Emergency Stop & Stuck-Key Failsafe:** Pressing physical `Esc` stops playback immediately. Every key and mouse button pressed by SmartMacro is tracked in `HeldInputTracker` and released when playback finishes, stops, pauses, or if an error occurs.
- **Administrator Apps (UIPI):** Windows blocks non-elevated processes from sending input to elevated (*Run as Administrator*) windows, the lock screen, or UAC prompts. Run SmartMacro as Administrator if your target game or app runs as Administrator.
- **Exclusive Fullscreen:** Desktop screen capture sees black in exclusive fullscreen games; switch the game to **Borderless Windowed** or **Windowed** mode for vision steps.
- **Anti-Cheat:** SmartMacro uses standard Win32 input and screen capture APIs (`SendInput`, `BitBlt`, `Windows.Media.Ocr`, `UIAutomation`) and never reads or modifies game memory or attempts to bypass anti-cheat software.

---

## Community & Support

- **Discord (Official Downloads, Bug Reports & Feedback):** https://discord.gg/CKBk5KnGmk
- **YouTube:** https://www.youtube.com/@KuroLogic039
- **Ko-fi (Optional Support):** https://ko-fi.com/sakurariku

© 2026 Riku · KuroLogic. All rights reserved.
