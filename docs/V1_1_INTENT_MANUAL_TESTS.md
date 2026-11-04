# SmartMacro v1.1 "Intent" — Manual Verification Checklist

Use these 5 scenarios to hand-verify v1.1 **Intent Recording**, **Multi-Scale Image Matching**, and **Adaptive Timing** on Windows.

---

## 1. Moved Window Relocation (`UI Automation` + `Locate Target`)

1. Open **Settings → Recording Defaults** and verify **🧭 Intent Recording** and **⏱ Smart Timing** are both **On**.
2. Open **Calculator** (or **Notepad** / a browser form) on the left half of your screen.
3. Record a short macro in **Smart** mode clicking 2–3 buttons (e.g., `7`, `+`, `5`, `=`) and stop recording.
4. Select one of the recorded **Mouse click** steps in the Editor:
   - Confirm the **✨ SMART CLICK & INTENT** card shows captured signals (e.g. `UI Button "Seven"` / `Window "Calculator"`).
   - Confirm **🧭 Smart Intent** is checked automatically.
5. Drag **Calculator** to the **opposite side of your monitor** (or onto a second monitor).
6. Click **🧭 Locate Target** in the Inspector:
   - Verify the green target-lock reticle flashes over the moved button on Calculator.
   - Verify the diagnostic report lists all 5 strategies (`1. UI Automation`, `2. OCR Text`, `3. Image Snapshot`, `4. Window-Relative`, `5. Coordinates`) with `✓` / `✗` and milliseconds.
7. Press **Play**: verify every button is clicked accurately in Calculator's new location.

---

## 2. Resized Window & Renamed Title (`Window-Relative` + `OCR Text`)

1. Open **Notepad** (`Untitled - Notepad`) at a compact size (e.g., `600×400`), record a click on a menu item or status-bar area, and stop recording.
2. Save or rename the file in Notepad so the window title changes (e.g., `notes.txt - Notepad`), and **resize + move** the Notepad window to `1200×800`.
3. Click **🧭 Locate Target** or press **Play**:
   - Verify SmartMacro still finds the Notepad window via its `notepad` process anchor (or `UI Automation` / `OCR Text`) and clicks the relocated target instead of the old screen coordinates.

---

## 3. Display Scaling / Resized UI (`AllowMultiScale` 75%–150%)

1. Create or open a macro with a **Find image** step (or a recorded click with **Smart Intent** + **Smart Click Snapshot**).
2. On a **Find image** step, check **🔍 Match across display scales & sizes (75%–150%)**.
3. Change browser zoom (e.g., `100%` → `125%` or `150%`) or move the app to a monitor with `125%` / `150%` Windows DPI scaling.
4. Click **Test Step** (or **🧭 Locate Target** on a click step):
   - Verify the target is matched and the result reports the matched scale (e.g., `at 125% scale` or `at 150% scale`).

---

## 4. Fast vs. Slow UI Load (`DelayMode.UntilScreenSettles` & `UntilScreenChanges`)

1. Record clicking a button or link that opens a dialog, tab, or page, pause ~`1000 ms` while it loads, then click inside the loaded UI.
2. Inspect the **Delay** step right after the first click:
   - Verify **Wait mode** was automatically set to **✨ Until screen settles** with sensible **Minimum wait (ms)** and **Max timeout (ms)** (`3×` recorded pause).
3. Play the macro when the UI responds **instantly** (cached / already warm):
   - Verify playback finishes the wait early (~`200–350 ms`) instead of waiting the full `1000 ms`.
4. Play the macro when the UI responds **slowly** (e.g., throttled network tab or heavy app startup taking `2000 ms`):
   - Verify playback waits while the area around the click is still changing, then clicks as soon as the screen settles.
5. Switch **Wait mode** to **⚡ Until screen changes** on a Delay step and verify playback wakes immediately as soon as new pixels appear.

---

## 5. 3D Game & Rhythm Regression Check (Zero Hook Lag)

1. Open a 3D game (e.g., Roblox with right-click or shift-lock camera) and record 15–20 seconds of fast camera movement + clicks in **Smart** mode.
2. Verify:
   - Zero mouse stutter or input lag while recording (all UIA, OCR, and PNG work runs strictly off the input hook / Raw Input thread, and full-window game canvases skip deep UIA walks).
   - Camera segments still become **Recorded input** steps with lossless relative mouse movement.
3. Record a short sequence in **🎵 Exact 1:1 / Rhythm** mode and verify no adaptive delays or path simplifications are applied.
