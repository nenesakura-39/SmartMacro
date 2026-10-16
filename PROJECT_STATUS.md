# SmartMacro – Project Status / Handoff

_Last updated: 2026-10-17 (8-bug fix batch: built, 616/616 tests passed, manual re-test pending). Earlier: Stages 1–3: built, automated tests passed, manually tested by the owner. Stage 4 (feedback
loops) and the Editor UX task: built, automated tests passed (573/573), **manual tests NOT done yet**. Not released.
Developer: Riku (リク) · Brand: KuroLogic_

## Current status at a glance (2026-10-16)
| Area | Build + automated tests | Manual tests | Committed |
|---|---|---|---|
| Stages 1–3 | passed (Stage 3 run: 389/389) | done by owner (reported "Done"; details for Stage 3 not sent) | yes (`ef78ce4`) |
| Stage 4 – feedback loops | passed: 0 warnings, 0 errors, 573/573 | **not done** (`docs/STAGE4_MANUAL_TESTS.md`) | yes (`bb18e56`, test fix `01d0168`) |
| Editor UX – Pick position, mouse editors, add/move steps | passed in the same 573/573 run | **not done** (`docs/EDITOR_UX_MANUAL_TESTS.md`) | **no** – uncommitted changes on top of `91b67ff` |
| Release documents (EULA, LICENSE, notices, README) | – | – | drafts; `release-docs/` not committed yet |
| Release package | `publish\SmartMacro-1.0.0-win-x64\SmartMacro.exe` is an OLD build (made right after `159d6b3`, before Stage 1) | – | ignored by git |


## 8-bug fix batch (2026-10-17) – built + tested on the owner's PC: 0 warnings, 0 errors, **616/616 tests passed**
Found by the owner's testing (screenshots + logs). Manual re-test of each bug is still to do. Not committed to git yet.
1. **Unreadable radio buttons** ("New steps go:"): `Themes/Controls.xaml` now has implicit dark styles for `RadioButton`
   (light text, accent ring/dot, hover/checked/focus/disabled), `ContextMenu`, `MenuItem` (with gesture text) and menu
   `Separator`. CheckBox / ComboBox / ComboBoxItem / ToolTip already had dark styles.
2. **OCR "0K" vs "OK"**: `OcrTextMatcher.Find(..., normalizeLookAlikes)` folds 0→O and 1 / l / | →I on both sides
   (before upper-casing) when the plain score is < 100 %. New `FindTextStep.NormalizeLookAlikes` (default on, also for
   old files), checkbox "Normalize similar characters (0/O, 1/I/l)". Applies to Find text, conditions and Test now.
   New "when the time is up" choice **Skip the next step, then continue** (`TextNotFoundBehavior.SkipNextStep`; skips
   the whole If / Repeat block when one starts there; a block end isn't skipped) + help text under the choice.
   Validator warnings: a disabled Find step with steps after it; "skip next" with nothing to skip.
3. **Type text dropped characters**: `TypeTextStep.CharacterDelayMs` defaults to 20 (also when a file doesn't store
   it). `Win32InputSimulator.TypeCharacters`: ≥ 6 ms key down→up, ≥ 8 ms between characters even at 0 ms;
   Space / Enter / Tab are sent as real keys (VK_SPACE etc.), not Unicode packets.
4. **Find image on round game UI**: `TemplateMatchOptions.IgnoreEdgeFraction` + `FindImageStep.IgnoreEdgesPercent`
   (0–40, checkbox "Ignore the edges (round buttons, game UI)" = 15 %). Shape and colour are compared on the inner
   part only (15 % removes all corners of a round button); matches still report the full rectangle. Matching was
   already brightness/contrast-normalized (NCC). Miss text suggests the option. DPI: captures and virtual-screen size
   now run inside `NativeMethods.PhysicalPixelsScope` (thread set to per-monitor-aware V2), so they are physical
   pixels on any thread; picker / cursor code already used physical pixels.
5. **Step flood / slider drags**: new `Core/Recording/MousePathSimplifier` (Douglas–Peucker 3 px + every real
   direction reversal per axis (jitter ≤ 3 px ignored) + rests ≥ 120 ms + first/last of each stretch). On for smart
   recordings (`SmartRecordingOptions.SimplifyMousePath`) and the app's desktop recordings; `RecordingOptions` default
   stays off. Timing kept (dropped moves' time goes into the next Delay). Playback: before a button down / up or wheel
   that follows a cursor jump, `Win32InputSimulator` nudges 1 px and back (real move events) and waits until the
   cursor has rested 20 ms at the target (only the missing part; never after relative / camera moves; double-clicks
   unaffected).
6. **Scroll wheel**: editor already had Direction + Number of notches. Playback (`PlaybackEngine.ScrollInNotchesAsync`)
   now sends one 120 event per notch, 15 ms apart on the timeline (remainder of odd amounts last). Recorded wheel
   events in "Recorded input" steps are sent as recorded. Wheel after a jump settles like a click.
7. **Game camera drift**: `GameRecordingOptions.MoveCoalesceUs` is capped at 8 ms (`MaxMoveCoalesceUs`). Playback
   already used one absolute timeline (PrecisePlaybackClock); new tests prove no drift with a timer that overshoots
   every wait by 1 ms (sequences and Delay steps).
8. **Alt+Tab recorded as "Press Left Alt"**: the hook already mapped WM_SYSKEYDOWN/UP. Real gap: keys pressed while
   SmartMacro's window is in front were dropped, so Alt+Tab into SmartMacro lost its Tab / Alt release. Both recorders
   now keep the rest of a combo that started in another app. Raw Input also treats WM_KEYUP / WM_SYSKEYUP in
   `RAWKEYBOARD.Message` as a release. Converters keep combos as Down(modifier) → Press(key) → Up(modifier) (tested
   for Alt+Tab, Ctrl+C, Shift+A, Alt+Tab+Tab, smart recording).
- Files: `MacroDocument.CurrentSchemaVersion = 7`, written only for "skip next step" or "ignore edges"; others keep 1–6.
- Tests: new `SmartMacro.Tests/BugFixTests.cs`. Updated on purpose: scroll expectations (one event per notch),
  `Files_from_a_newer_version_than_the_current_one_are_refused` (no hardcoded 6), v1-file test (20 ms typing default).
- Manual re-test to do: radio buttons readable; Find text "OK" on a dialog showing "0K"; type "Found Wow" in Notepad
  at 0 and 20 ms; Roblox Fisch SHAKE with "Ignore the edges"; Roblox volume slider 80 % → 50 % recording; scroll
  3 notches in Chrome + Roblox zoom; game-mode camera run; record Alt+Tab.

## Done
**Phase 1: Foundation**
- Dark navy/charcoal theme, sidebar navigation (Library, Workflow Editor, Logs, Settings), status bar, Activity panel.
- Macro Library: create, open (double-click / Enter), duplicate, delete (moved to Trash), search, reload, open folder.
- JSON storage in `%LOCALAPPDATA%\SmartMacro` (Macros, Trash, Logs, settings.json).
  - Atomic saves (temp file + replace) and a `.bak` copy of the previous version.
  - Damaged or newer-version files are reported in a banner and the log. They are never deleted.
  - Duplicate or missing IDs are repaired automatically. Only known step types can be loaded (no arbitrary types).
- Settings page (confirm before delete, default delay, show Activity panel), saved to settings.json.
- Global error handling: unexpected errors are logged and shown instead of crashing the app.

**Phase 2: Workflow editor**
- Step types: Delay, Move mouse, Mouse click, Scroll wheel, Key press (with Ctrl/Shift/Alt/Win), Type text.
- Add (inserted after the selection), delete, duplicate, move up/down, enable/disable, comment.
- Properties panel for each step type, with live validation and a Problems list. Save is blocked while there are errors.
- Unsaved-changes tracking: Discard changes, and a Save / Discard / Cancel prompt when switching macros or closing.
- Shortcuts: Ctrl+S save, Delete, Ctrl+D duplicate, Alt+Up/Down move.

**Phase 3: Recording and playback** (owner tested: record + play in Notepad, repeat-until-stopped, Esc emergency stop all worked)
- Automation bar (Record / Stop recording / Play / Pause-Resume / Stop), with a state pill, progress text and a progress bar.
- Recording uses low-level hooks (WH_MOUSE_LL / WH_KEYBOARD_LL). `RecordingConverter` turns the raw events into steps:
  - quick taps become Click / Press steps
  - holds and drags become Down / Up steps
  - gaps become Delay steps
  - mouse movement is thinned out
  - the stop shortcut is trimmed from the end
  - releases of keys/buttons pressed before recording started are dropped
  - anything still held at the end is released
- Each recording is saved as a new macro "Recording yyyy-MM-dd HH:mm" and opened in the editor.
- Playback: `PlaybackEngine` (in Core, unit tested) runs on a background thread through `Win32InputSimulator` (SendInput / SetCursorPos).
  - Supports repeat (0 = until stopped), speed, pause/resume and stop.
  - Held keys and buttons are always released at the end.
  - The Windows timer is set to 1 ms during playback so short delays are accurate.
- Play runs the macro open in the editor (including unsaved edits); if none is open, it runs the library selection. Macros with errors are refused.
- Safety:
  - Physical Esc during playback triggers an emergency stop (injected Esc is ignored).
  - Global shortcuts: Ctrl+Shift+F9 record, F10 play/stop, F11 pause.
  - A click-through, always-on-top status banner is shown, and the main window is minimized (optional).
  - Closing the app stops any run and waits for held keys to be released.
- Input generated by SmartMacro is tagged (dwExtraInfo) so the recorder ignores it. Clicks on SmartMacro's own windows aren't recorded.
- New settings: minimize during run, status banner, record mouse movement. New per-macro fields: RepeatCount, Speed and RepeatDelayMs ("Wait between repeats", added after testing showed loops ran back-to-back). Old files get defaults.

**Phase 4: OCR** (owner-tested and working: full-screen find, picked area on a dual-monitor setup, find + type. Fixes added after testing, not yet built: SmartMacro's window is excluded from captures during runs and tests, small areas get padded and upscaled 2x, the app waits 400 ms before restoring its window so typed text isn't cut off, there's a 100 ms settle after clicking found text, and the not-found error shows the text OCR actually read)
- New step **Find text (OCR)**. It looks for text on the whole screen or in a picked area, using the text recognition built into Windows (`Windows.Media.Ocr`).
  - Options: Contains or whole words, case-sensitive, match accuracy %, how long to keep looking (0 = look once), and how often to check.
  - When found, it can just continue, move the mouse to the text, click, double-click or right-click it, with an X/Y offset.
  - If the text isn't found, it can either stop the macro with an error or continue.
- Mouse click steps have a new option, "Click on the text found by the last Find text step".
- Editor tools:
  - **Pick area on screen**: SmartMacro hides, and you drag a rectangle across any monitor.
  - **Test now**: SmartMacro hides, reads the area once, and shows the match or the text it read.
- Area picker: one overlay per monitor, like Snipping Tool. The first version stretched one window across both monitors and recorded wrong coordinates on a dual-monitor setup with different display scaling. Capture margin is 48 px. "Test now" saves `Logs\last-ocr-test.png` showing exactly what was captured.
- `OcrTextMatcher` (Core) checks spans of 1 to n+1 words and scores them by edit-distance similarity. Windows OCR gives no confidence value.
- Screen capture uses GDI BitBlt into BGRA, then `SoftwareBitmap`. Large areas are split into tiles of at most `OcrEngine.MaxImageDimension`, with 120 px overlap.
- The status banner uses `WDA_EXCLUDEFROMCAPTURE`, so OCR shouldn't read it. Playback is refused with an explanation if a macro uses Find text and no OCR language is installed.
- Settings shows the OCR status and language.

**Phase 5: Integration & release** (written, not built yet)
- Dark pop-up dialogs (`DarkMessageBox`) replace the Windows message boxes. Unsaved-changes buttons now read Save / Don't save / Cancel.
- **Custom shortcuts:** set in Settings → Keyboard shortcuts (click a box, press a combo).
  - `HotkeyBinding` (Core) checks them: a modifier is required unless it's an F-key, Esc and F12 aren't allowed, and duplicates are rejected.
  - `HotkeyService.Apply()` re-registers the shortcuts when settings are saved. Shortcuts another program already uses are reported.
  - The recording trims whatever the current record shortcut is.
- **About** card in Settings: name, version, "Owner & developer: Riku", and **Copy diagnostics** (version, OS, screen, OCR status, recent log; no macro contents).
- **Library:**
  - **Play** on every row and in the details panel (plays the saved version).
  - **Import** (several files at once): each file goes through `MacroFileFormat.TryParse` (only known step types), gets fresh IDs and a unique name, and is validated. Skipped files are listed with a reason.
  - **Export…** saves a `.smartmacro` file.
- **Single instance:** a second launch brings the running copy to the front (named mutex + event).
- **App icon:** `Assets/SmartMacro.ico` (blue rounded square with a lightning bolt). The sidebar shows the version and owner.
- **Release:** `tools\publish.cmd` runs the tests, then publishes a self-contained single-file win-x64 build. It zips it to `release\SmartMacro-1.0.0-win-x64.zip` with `README.md` (user guide).
- **Cleanup:** removed duplicate interop (ScreenInfoProvider now uses NativeMethods). The repository and import share one file parser.

**Phase 6: Game input fidelity** (written, NOT built or tested yet — no .NET SDK in the AI's environment)
- Diagnosis (confirmed in code):
  1. Desktop recording stores absolute cursor positions and plays them with `SetCursorPos`. That never produces raw mouse movement, which is what first-person games (cursor locked) read for camera rotation.
  2. `RecordingConverter` turned any key/button down+up within 500 ms into an instant Press/Click: the hold time was dropped (a 120 ms W tap became ~0 ms). Mouse moves during that tap were emitted after the release with no delay (wrong order, squashed timing).
  3. Gaps shorter than `MinDelayMs` (15 ms) were thrown away, not carried over, so recordings got shorter. 30 ms move thinning is fine for absolute positions but would lose movement if applied to relative deltas.
  4. Every Delay step measured its own time, so timer overshoot (~1 ms per wait) added up over long macros. Held keys stayed down while paused.
- Fixes:
  - `KeyPressStep.HoldMs` / `MouseClickStep.HoldMs` (0 = old instant behavior). The converter fills them in (`RecordingOptions.KeepHoldTime`, Settings checkbox, on by default). Taps only collapse into one step when nothing else is recorded in between. Short gaps are carried into the next Delay. The last thinned-out cursor position is kept.
  - `PlaybackEngine` schedules delays, holds and recorded events on one deadline timeline (no drift; skips ahead if >250 ms behind instead of bursting). Pause releases held keys/buttons and presses them again on resume (`ReleaseHeldInputWhilePaused`); the timeline shifts by the paused time.
  - New **game recording mode** (Settings → Recording mode): `RawInputRecorder` (Raw Input / WM_INPUT on its own thread with a message-only window, RIDEV_INPUTSINK) records relative mouse counts, buttons, wheel, and keys with scan codes, with µs timestamps. `GameRecordingConverter` keeps every sample (no thinning), trims the stop shortcut, drops early releases/auto-repeat, releases anything still held.
  - New step `InputSequenceStep` ("inputSequence", editor: read-only summary + optional "move cursor to start"). Events stored as a compact string (`InputEventCodec`, "v1 dt,code,args …"); damaged data is refused on load and by the validator.
  - Playback: `IInputSimulator.MoveMouseRelative` (SendInput MOUSEEVENTF_MOVE) and `KeyWithScanCode` (recorded scan code + extended flag, so right Shift stays right Shift). `PrecisePlaybackClock` uses a high-resolution waitable timer for sub-ms waits.
  - File format: `MacroDocument.CurrentSchemaVersion = 2`. A macro is written as version 2 only if it uses hold times or recorded-input steps; everything else is still written as version 1. Version 1 files load unchanged (new fields default to 0/off).
- Not verified: whether Roblox (or any specific game) accepts SendInput relative mouse movement and keys. Unit tests only prove what SmartMacro sends and when.

**Phase 6 audit** (2026-10-16; written, not yet rebuilt)
- Owner's run of Phase 6 before this audit (`tools\verify.cmd`): build 0 warnings / 0 errors, 197/197 tests passed. Owner manually confirmed in Roblox RIVALS (game mode) that the camera turns left/right and movement/clicks play back; precision varies with starting position.
- Defect: `KeyWithScanCode` sent wVk + wScan without KEYEVENTF_SCANCODE, so Windows identified the key by the generic virtual key (e.g. VK_SHIFT) and the recorded scan code was only extra info. Fixed: game-mode keys use scan-code mode (KEYEVENTF_SCANCODE, wVk = 0, EXTENDEDKEY for E0 keys, same flags on release). Encoding rules moved to `Core/Input/KeyboardInputEncoder` (unit tested); desktop steps still use virtual-key mode, unchanged.
- Defect: `RawInputRecorder` kept window handle, buffer, event list and start error in shared fields. A session that didn't stop within 2 s could free the next session's buffer, clear its window handle (so it couldn't be stopped) or unregister its raw input. A start timeout left the thread running and registered. Fixed: each recording is its own `Session`; only the session that owns the Raw Input registration may remove it; a stop/timeout before the window exists is honoured by the thread.
- Recorder: E1 keys (Pause) recorded without a scan code (sent by virtual key); keyboard overrun code ignored; handler errors are logged after recording.
- `tools\verify.cmd`: stops when the build fails (tests not run), deletes old logs first, writes a .trx, returns a non-zero exit code on failure.

**Roblox UI click fix** (2026-10-16; written, not yet rebuilt)
- Owner's run after the Phase 6 audit: build 0 warnings / 0 errors, 225/225 tests passed (`artifacts\test-results.trx`).
- Symptom: in windowed Roblox the cursor reaches the button and title-bar clicks work, but Roblox's own UI buttons don't react reliably.
- Cause (most likely, from the code path): `MoveMouse` only called `SetCursorPos`. That teleports the cursor without creating a mouse-input event, and button presses are sent without a position, so an app that tracks the pointer from the input stream (Raw Input) still has the old position when the click arrives. Windows' own title-bar handling uses the real cursor position, which is why that worked.
- Fix: `MoveMouse` now sends a SendInput absolute move (MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK) to the target, then `SetCursorPos` for the exact pixel. Applies to every step that moves the mouse (Move, Click/Scroll at a position, Find text clicks, game-mode "move cursor to start"). Pixel math in `Core/Input/AbsoluteMouseMath`.
- Diagnostics: `Win32InputSimulator` logs "Input check:" lines (max one per 2 s) when the window under the cursor isn't active, the cursor is confined (ClipCursor, games locking the mouse), or the cursor didn't end up where it was sent.
- Right-button camera turning: a Desktop recording can't reproduce it (the game recentres the locked cursor, so absolute positions barely change). Use Game mode, which records relative movement.
- Not verified in Roblox yet.

**Stage 1: Unified smart recording** (2026-10-16; written, NOT built or tested yet — the AI's environment had no .NET SDK)
- Goal: one Record button for apps and games (no Desktop/Game choice first). Desktop / Game modes stay as advanced fallbacks.
- Audit before the change: the owner's last run (after the Roblox UI click fix) was build 0 warnings / 0 errors, 225/225 tests.
  Desktop mode = low-level hooks (ms timestamps, absolute positions) → `RecordingConverter` → editable steps. Game mode =
  Raw Input (µs Stopwatch timestamps, relative counts, scan codes) → `GameRecordingConverter` → one `InputSequenceStep`.
  The two used different clocks and saw the same physical events, so they were NOT merged; one source is used instead.
- Recorder: `RawInputRecorder(captureCursor: true)`, wrapped as `SmartInputRecorder` (`ISmartInputRecorder` in Core).
  One Raw Input session, one clock. Each mouse packet also stores `GetCursorPos` (physical pixels) and cursor state flags
  (`CursorFlags`: Hidden via GetCursorInfo, ConfinedToSpot via GetClipCursor ≤ 8×8 px, AtEdge when the cursor is stuck on a
  monitor/clip edge in the direction of movement). Cursor state is re-read at most every 8 ms or on button changes.
  Clicks on SmartMacro's own windows are dropped. Game mode records exactly as before (flag off).
- Model: `InputEvent` gained optional `HasPosition, X, Y` (mouse events only). Event text format `u1` = v1 codes plus
  optional `,x,y` on m/bd/bu/w/hw. Lists without positions are still written as `v1` (game recordings byte-identical).
  `InputSequenceStep.MouseSegments` (null for game mode): sorted `MouseSegment { StartUs, Detected, Evidence, Choice }`,
  `Effective` = Choice or Detected. Modes: `CursorPosition` / `Relative`.
- Detection: `Core/Recording/MouseContextClassifier`. Per mouse sample: hidden/confined → relative; otherwise compare the
  cursor's bounding-box size with the net relative movement in the 150 ms before and after (locked: ≤ 3 px + 12 %, free:
  ≥ 25 %); if they disagree, the sample's own movement decides (mouse moved, cursor didn't → relative). Unclear samples
  inherit. Parts < 60 ms are merged. Presses/wheel with a visible cursor right after camera movement (or before the first
  movement) are put in a cursor part (releases are not, so a camera drag's release never moves the cursor). A held
  button is NOT treated as camera movement on its own.
- Conversion: `Core/Recording/SmartRecordingConverter`. Clean-up like game mode (trim stop shortcut, drop early
  releases / auto-repeat / duplicate presses / empty moves, release anything held at the end). The timeline is cut only
  where nothing is held. Cursor-only pieces → editable steps through the existing `RecordingConverter` (generic
  Shift/Ctrl/Alt mapped to left/right VKs from the scan code). Pieces with camera movement → one `InputSequenceStep` with
  every event, both mouse representations and its mouse segments. Short (< 300 ms) cursor-only movement next to a camera
  part stays inside that step. Delay steps between pieces are computed from the played timeline (no rounding drift).
  Conversion runs on a background thread. A pure desktop recording gives the same kind of steps as Desktop mode.
- Playback (`PlaybackEngine.PlaySequenceAsync`): no segments → relative only (old behaviour). With segments, each event
  uses its segment: cursor parts call `MoveMouse(x, y)` (skipping repeats; always before a press/wheel; before a release
  only if needed), relative parts call `MoveMouseRelative`. Exactly one representation per event, never both.
  The position cache is cleared after a pause/stall. Stop / Esc / failure release everything as before.
- Editor: "Recorded input" step shows Mouse playback parts (time range, detected mode + reason, drop-down
  Automatic / Cursor position / Relative, "Plays as"), plus All: cursor / relative / automatic buttons. Game-mode steps
  keep the old view and "move cursor to start" option.
- Files: `MacroDocument.CurrentSchemaVersion = 3`. Written as 3 only when a step has mouse segments or positions; game
  recordings stay 2, plain macros stay 1. Damaged u1 text, out-of-range positions (> 100,000 px), unsorted / unknown
  segments are refused or reported. Older SmartMacro builds refuse v3 files with the "newer version" message.
- Settings: `RecordingMode.Smart` (new default). `AppSettings.CurrentSchemaVersion = 2`; version-1 settings files are
  switched to Smart once. Settings page lists Smart (recommended), Advanced – Desktop only, Advanced – Game only, with help
  text; "record movement" / "keep hold time" apply to editable steps of Smart and Desktop.
- Tests: `SmartMacro.Tests/SmartRecordingTests.cs` (24 tests, 29 cases: desktop-only → editable steps, edge pushing, hide-while-typing,
  hidden-cursor camera, right-drag camera with hold time and mode switches, user override, W held across camera
  movement with scan code, click → camera → click timeline, equal timestamps, stop shortcut trim, early releases /
  auto-repeat / duplicates, release at end, L/R modifiers, stop releases held button, legacy relative playback, codec
  round trip + damaged text, schema 3/2, newer-version refusal, segment validation, settings migration).
- Docs: `docs/STAGE1_MANUAL_TESTS.md` (beginner checklist: Notepad + games), `docs/ROADMAP_STAGES_2_4.md` (plan only).
- Not verified: build, test run, any real app or game. Detection thresholds are first guesses and need real-world tuning.
- **Update:** the owner then ran `tools\verify.cmd` and the Stage 1 manual tests and reported everything green
  ("100/100"). The exact test count from `artifacts\test.log` wasn't sent, so it isn't recorded here.

**Stage 2: Reusable visual detection** (2026-10-16; written, NOT built or tested yet — the AI's environment had no .NET SDK)
- New Core folder `SmartMacro.Core/Vision` (no Windows code, all unit tested with fake screens):
  - `BgraImage` (BGRA pixels, crop / fill / draw rectangle), `ScreenFrame` (picture + its screen bounds), `RgbColor`
    ("#RRGGBB"), `TemplateCodec` (picture stored in the macro: BGR bytes → zlib → Base64; size and checksum checked).
  - `IScreenCapture` (capture an area in physical pixels; returns the on-screen part), `DetectionTimer` (capture/search
    time, "busy %" for the log), `DetectionTimeoutException`, `ScreenCaptureUnavailableException`.
  - `TemplateMatcher`: grey NCC, coarse pass on a shrunken copy (1/2, 1/4, 1/8 chosen by a ~40 M operations budget,
    template ≥ 8 px after shrinking), local peaks spread apart, exact refine at full size, average-colour check
    (≤ 48 per channel) unless IgnoreColor, overlapping matches removed, best first. Reports BestScore even on a miss and
    how many shape matches were rejected for colour. Rejects flat (one-colour) templates.
  - `ColorFinder`: per-channel tolerance mask + 4-neighbour flood fill → patches (bounds, size, centroid), biggest first.
  - `MarkerTracker`: marker position 0–100 % along a horizontal / vertical area, Found / Ambiguous / Lost / IgnoredJump
    (one-frame jumps > 35 % are ignored once), confidence = share of marker-coloured pixels in the chosen patch.
- Models (`MacroStep.cs`): new abstract `ScreenSearchStep` (area, TimeoutMs, CheckIntervalMs, **Goal** Appear/Disappear,
  Action, Offset, WhenNotFound) shared by `FindTextStep` (unchanged JSON names), new `FindImageStep` (TemplateWidth /
  Height / Data, MinSimilarityPercent 90, IgnoreColor, check every 250 ms) and `FindColorStep` (Color "#RRGGBB",
  Tolerance 24, MinPixels 10, check every 250 ms). JSON types "findImage" / "findColor".
- Engine: one shared wait loop `SearchAsync` for all three (appear / disappear, timeout, continue-or-stop, pause / stop,
  click settle). **Paused time no longer counts toward the timeout.** One log line per step with capture/search
  averages, never per frame. "Last found" is now a point shared by all three steps. `PlaybackEngine` takes an optional
  `IScreenCapture` (4th constructor argument); without it, image/colour steps fail with a clear message.
- Validation: template present / 6–400 px / decodes / not flat; area ≥ template; similarity 50–100 (warning < 70);
  check interval ≥ 50 ms for image/colour (OCR stays ≥ 100); colour must parse; tolerance 0–255 (warning > 100);
  warning for whole-screen colour search; warning if a "when found" action is set while waiting for disappear.
- Files: `MacroDocument.CurrentSchemaVersion = 4`. Written as 4 only when a Find image / Find color step or a Find text
  with Goal = Disappear exists. `RequiredSchemaVersion` now takes the highest need of all steps (it used to return 3 as
  soon as it saw a smart recording, even if a later step needed more). Old files load unchanged (Goal defaults to Appear).
- App: `Services/Vision/GdiScreenCapture` (reuses the OCR GDI capture), `Services/Vision/VisionTools` (Pick area,
  Capture image, Pick color = one click, Test now with a preview window + `Logs\last-image-test.png` /
  `last-color-test.png`, Track for 3 s ≈ 30 fps max with positions, fps and ms per frame). The region picker got a
  custom hint text and a one-click "pick point" mode. Heavy work runs on background threads; SmartMacro hides and is
  excluded from capture while it looks.
- Editor: `ScreenSearchStepViewModel` base (shared fields, busy handling) + `FindImageStepViewModel` (preview of the stored
  picture) + `FindColorStepViewModel` (swatch). XAML: shared keyed templates `SearchAreaTemplate`, `SearchWaitTemplate`,
  `SearchResultTemplate`, `SearchTestResultTemplate`; Find text now has "Wait for: appears / disappears".
- Tests: `SmartMacro.Tests/VisionTests.cs` (codec, matcher incl. coarse path / multiple copies / colour check /
  brightness, colour finder, tracker, engine with `FakeCapture` incl. negative monitor coordinates, timeout, continue,
  disappear, stop, validation, schema 4 round trip, old-file loading, descriptions). The matcher maths was also
  sanity-checked with a NumPy port of the same algorithm (not a substitute for running the C# tests).
- Docs: `docs/STAGE2_MANUAL_TESTS.md`, README section "Find image and Find color", roadmap updated with Stage 3/4 breakdown.
- Owner build (2026-10-16): 0 warnings, 0 errors. Tests: 317/318 — the one failure was the Stage 1 test
  `Files_from_a_newer_version_are_refused`, which hardcoded format 4 as "newer". Fixed to use CurrentSchemaVersion + 1;
  re-run pending.
- **Update:** owner re-ran verify (all green) and did the STAGE2 manual tests: all passed ("100/100"). Owner noted some
  improvements are wanted; they are deferred to a polish pass at the end of the roadmap (not listed yet — ask the owner).

**Stage 3: Conditional workflows and loops** (2026-10-16; written by the AI without a .NET SDK, then built and tested by the owner)
- Models (`Core/Models/WorkflowSteps.cs`): `Condition` { `Check` = a FindTextStep / FindImageStep / FindColorStep
  (stored with the normal step discriminators, so only known types load), `Negate` } with derived `Kind`
  (TextVisible / ImageVisible / ColorVisible). Conditions use the search's what / area / TimeoutMs ("keep checking
  for", 0 = look once) / CheckIntervalMs; its Goal / Action / Offset / WhenNotFound are ignored.
  Block steps in the flat list: `IfStep` (Condition), `ElseStep`, `EndIfStep`, `RepeatStep` (Mode Times / While /
  Until, Count, Condition, MaxIterations default 100, MaxDurationMs, WhenLimitReached StopMacro / ExitLoop),
  `EndRepeatStep`. JSON types "if", "else", "endIf", "repeat", "endRepeat". Helpers `WorkflowSteps.*`.
- `Core/Workflow/BlockStructure`: one pass with a stack → indentation depth per step, jump table (If → Else / End If,
  Else → End If, Repeat ↔ End Repeat) and plain-English problems (missing End, End without start, Else outside an If /
  inside a Repeat, two Elses, overlapping blocks, more than 8 levels). Disabled steps count for structure.
- Detection reuse: the per-type probe code moved out of the engine into `Core/Playback/ScreenProbes` (`PreparedSearch`
  = decoded template + area, `LookAsync` looks once). `SearchAsync` (Find steps) and the new
  `IConditionEvaluator` / `ScreenConditionEvaluator` (one look, cached per condition per run) both use it.
- Engine: index-based program counter (`RunStepsAsync`), loop frames on a per-run stack (turns, start time, last check).
  If: condition waits up to its timeout (paused time excluded via `_pausedTotal` / `ActiveTime`), true → next step,
  false → after Else or End If. Disabled If / Repeat skip their whole block; disabled markers are ignored (warning).
  Repeat: checked before every turn — count, max turns, spacing (checks ≥ "check every" apart, so empty loops can't
  spin), max time, then the condition. Limit → `LoopLimitException` (Failed) or leave the loop. A condition that sees
  its target sets the "last found" spot. Logging: If answers only when they change; one line when a loop ends.
  `PlaybackProgress.LoopText` ("loop turn 2 of 5") is shown in the progress text. Held input released on every exit.
- Validation: block problems are errors (Save + Play refused); Repeat count 1–1,000,000; Max turns 0–1,000,000;
  Max time 0–24 h; While / Until need a condition and a limit (not both 0); condition search validated with the Find
  step rules (messages "Condition: …"); warnings for empty blocks and disabled markers; conditions count as "find
  before" for last-found clicks.
- Files: `MacroDocument.CurrentSchemaVersion = 5`, written only when a block step exists (others stay 1–4).
- App: toolbox "Logic" section; If / Repeat add their End; list allows Shift/Ctrl multi-select; "Wrap in If" /
  "Wrap in Repeat" (Ctrl+Shift+I / R) wrap a contiguous, self-contained selection; deleting a lone If / Repeat / End
  removes the block's markers but keeps the inner steps; rows indented 22 px per level. Property editors: `IfStepViewModel`,
  `RepeatStepViewModel`, `BlockMarkerStepViewModel`, `ConditionViewModel` (kind + is / is NOT + the normal Find editor
  embedded with `IsStandalone = false`, which hides header, goal, action and comment). OCR availability check also covers
  text conditions.
- Tests: `SmartMacro.Tests/ConditionalWorkflowTests.cs` (block analyser incl. every broken shape and depth 8/9; true /
  false / NOT / timeout / pause-inside-wait; nested; Times / While / Until; max turns stop & continue; max time; check
  spacing; stop inside a loop and during a condition wait with release; pause in a loop; progress text; evaluator;
  validation; format 5 round trip, old v4 file, damaged / script-like files, broken structure, import).
- Docs: `docs/STAGE3_MANUAL_TESTS.md`, README "If / Else and Repeat", roadmap status.
- Owner build (2026-10-16): `tools\verify.cmd` → 0 warnings, 0 errors; tests **389/389 passed** (first run, no fixes needed).
  Owner then reported the Stage 3 work "Done" (manual-test details not sent; ask if anything failed).

**Stage 4: Continuous visual feedback** (2026-10-16; written by the AI without a .NET SDK, then built and unit-tested by the owner – manual tests still open. The original note follows. The AI's environment had no .NET SDK
and no network access to install one, so nothing below has been compiled or run)
- Audit before the change: Stage 2 `MarkerTracker` (Found / Ambiguous / Lost / IgnoredJump) was only used by the editor's
  "Track for 3 s". Stage 3 gave the program-counter engine, `HeldInput` (released on every exit, released / re-pressed on
  pause), `ActiveTime` (paused time excluded), `IConditionEvaluator` + `ScreenProbes`. Stage 4 builds on exactly these.
- Model (`Core/Models/FeedbackLoopStep.cs`, JSON "feedbackLoop", `StepKind.FeedbackLoop`): bar area (RegionX/Y/Width/
  Height, default 300×30), `Orientation` (Horizontal / Vertical), MarkerColor / MarkerTolerance 24 / MarkerMinPixels 3 /
  MaxJumpPercent 35; ZoneMode Fixed (ZoneStartPercent 40, ZoneEndPercent 60) or DetectedByColor (ZoneColor / ZoneTolerance
  / ZoneMinPixels 20); rules WhenBeforeZone Hold / WhenInsideZone Keep / WhenAfterZone Release (Hold / Release / Keep /
  Tap); DeadBandPercent 2; InputKind MouseButton (Left) or Key (VirtualKey, default Space); TapMs 40;
  MaxCapturesPerSecond 30; MinInputChangeMs 50; MaxRuntimeMs 30,000 (required) + WhenTimeUp (ExitLoop default /
  StopMacro); LostTimeoutMs 500 + WhenTargetLost (ReleaseAndWait default / ExitLoop / StopMacro); optional CompleteWhen and
  FailWhen (Stage 3 `Condition`) + WhenFailed (StopMacro default / ExitLoop). Plain data: no scripts, no expressions.
- `Core/Vision/ZoneFinder`: biggest patch of the zone colour along the bar, joined with neighbours across gaps up to the
  marker's width + 2 px (a marker on top of the zone doesn't split it); % uses the same scale as `MarkerTracker`.
- `Core/Playback/FeedbackSensor`: capture bar area → `MarkerTracker.Update` → fixed zone or `ZoneFinder` →
  `FeedbackObservation` + capture / detect times. Refuses a bar area that is partly off screen (percentages would shift).
  Shared by the engine and the editor tools. `FeedbackStats` = counters + the one-line summary.
- `Core/Playback/FeedbackController` (pure, caller passes the time): untrusted frame (lost / ambiguous / ignored jump /
  zone missing) → no change; lost ≥ LostTimeout → release-and-wait (safety release ignores the min change time) or
  TargetLost for the runner; else classify before / inside / after with the dead band (only switch region when the marker
  is DeadBand % past the crossed edge; a jump right across the zone that isn't clearly past the far edge counts as
  inside), wanted state from the rule, Tap only on a real move into the region (not at the start or when the target comes
  back), changes held back until MinInputChange has passed (Deferred).
- Engine (`PlaybackEngine.RunFeedbackLoopAsync`, called from `RunStepsAsync`): per frame → pause / stop check → max runtime
  (`ActiveTime`, paused time excluded) → FailWhen then CompleteWhen (each looked at once, spaced by its "check every"; a
  condition that sees its target sets "last found") → sense → decide → act through `HeldInput` (Press / Release / Tap =
  release if held, press, TapMs, release) → progress text every 250 ms ("feedback 3.2 s, holding") → rate limit wait.
  Takes over the key / button at the start (released if a previous step held it). `finally`: release, `ResetTimeline`,
  ONE log line (why it ended, frames, achieved Hz, avg capture / detect ms, slowest frame, reaction-time range, input
  changes, deferred changes, marker found / unclear / missing %, jumps ignored, zone missing %, lost episodes, condition
  checks). StopMacro endings throw `FeedbackLoopException` (macro Failed). Macro Speed doesn't apply (real time).
  `WaitAsync(TimeSpan)` overload added (exact waits); the old `WaitAsync(double ms)` forwards to it.
- Validation (`ValidateFeedbackLoop`): unknown enum values; area 5×5 to 20,000 and ≥ 10 px along the bar; warning for
  areas > 250,000 px or off screen; colours; tolerances 0–255 (warning > 100); min sizes; glitch filter 1–100; fixed zone
  0 ≤ start < end ≤ 100 (warning when dead band ≥ half the zone); detected-zone colours too close to the marker (warning);
  dead band 0–25; at least one rule Hold or Tap (error) and a warning for hold-without-release; valid mouse button / key
  (warning for Esc); tap 1–1,000 ms; captures/s 1–120 (warning > 60); min change 0–5,000 (warning < 16); max runtime
  100 ms–1 h (required); lost timeout 0–60 s; conditions validated like If conditions with labels "Done when:" /
  "Failed when:" (warnings: OCR is slow inside a loop, "keep checking for" unused); warning when the loop can only end with
  an error. `ValidateCondition` got optional label / property parameters (Stage 3 messages unchanged except "Condition:
  choose what…" wording for a missing search). `WorkflowSteps.ConditionsOf` (If / Repeat / feedback conditions);
  `ScreenSearches` and the "find before last-found click" check use it, so the OCR availability check covers text
  conditions in feedback loops.
- Files: `MacroDocument.CurrentSchemaVersion = 6`, written only when a Feedback loop exists (others keep 1–5).
  Factory, describer ("Keep marker #FF0000 in zone 40–60 %: hold before, release after (left button; max 30 s)").
- App: toolbox entry "Feedback loop"; `FeedbackLoopStepViewModel` (all settings, Done / Failed checkboxes creating colour
  conditions, embedded condition editors, Pick bar area – also sets the direction from the shape –, Pick marker / zone
  color, Measure, Preview); XAML template with 6 numbered sections. `IVisionTools` / `VisionTools`: **Measure loop
  speed** (3 s, capture + detect back to back, no input; fps, avg / p95 / slowest ms, found %, suggested captures/s =
  60 % of full speed clamped 5–60; picture with overlay) and **Preview 10 s** (real sensor + controller at the step's
  rate, 1 ms timer, NO input; live always-on-top, non-activating, capture-excluded window at the bottom right with the
  bar + overlay + "would PRESS / RELEASE / TAP", Stop button / close = stop; summary with would-be presses, held share,
  where the real loop would have ended). Tools work on a copy of the step.
- Tests: `SmartMacro.Tests/FeedbackLoopTests.cs` — controller (hold / keep / release, changes only on change, dead band,
  Classify table, untrusted frames, missing zone, lost → release-and-wait / exit / stop, lost timer from start, min
  change defers, safety release, tap rules, deferred tap, direction changes, moving zone); zone finder (edges, bridging,
  missing, vertical); sensor (fixed / detected zone, ambiguity, partly off screen, bad colour); stats text; engine with a
  scripted fake bar (sweep, rate limit, max runtime stop / continue, done, fail stop / continue, fail wins, lost exit /
  stop, release-and-wait, unclear / short gaps, jump ignored / accepted, min change spacing, tap timing, key input,
  moving detected zone, takeover of a held button, Stop (fake + real clock), Pause release / re-press with paused time not
  counted, no capture, off-screen bar, disabled step, inside Repeat, progress text, last-found); validation (every
  setting, unknown enums, warnings, off screen, refused before running, OCR detection, factory / describer); files
  (format 6 round trip, older formats kept, unset conditions not written, formats 1–5 load unchanged, minimal file
  defaults, damaged JSON refused, loadable-but-unsafe values refused by the validator, newer than 6 refused).
- Docs: `docs/STAGE4_MANUAL_TESTS.md` (beginner checklist, measured numbers to send back), `docs/feedback-test-page.html`
  (offline test page: mode 1 moving marker over a fixed zone, mode 2 catch game with a white zone pushed by mouse / Space,
  simple or heavy physics, green #00C800 / blue #0000C8 status square), README "Feedback loop", roadmap "Stage 4 as built".
- Design check (not a substitute for the C# tests): a Python simulation of the test page's catch game with the same
  bang-bang + dead-band rules won 190–200 of 200 rounds with the "simple" zone at 15–60 captures/s and 2–6 frames
  of latency, but only 2–47 % of rounds with the "heavy" (momentum) zone. That's the documented honest limit.
- Not verified: build, test run, any measurement on a real PC, any app or game.

**Editor UX: Pick position, clearer mouse steps, adding and moving steps** (2026-10-16; built and unit-tested by the owner – manual tests still open; originally written without a .NET SDK —
no .NET SDK in the AI's environment)
- Audit: Move mouse / Mouse click / Scroll wheel already used physical virtual-screen pixels everywhere (GetCursorPos,
  SetCursorPos + SendInput absolute move, per-monitor DPI aware app, recording uses the same). Playback: Move = instant
  jump; Click = jump to X/Y (unless "current position" / "last found") then down/up (HoldMs, double, down, up); Scroll =
  optional jump to X/Y, then one wheel event of `Delta` (120 = 1 notch, + = up/right). `MouseScrollStep` already had
  `UseCurrentPosition` + X/Y, so positional scrolling needed **no model, file-format or engine change**. The OCR/vision
  `RegionPicker` already had a one-click point mode (Pick color) – reused.
- Pick position: `IVisionTools.PickPositionAsync` (hide SmartMacro, one overlay per monitor, crosshair + live
  "X …, Y …" label, Esc / right-click cancel) and `GetCursorPosition`. Point mode now picks on **button release**, so
  both press and release land on the overlay (before, the release went to the app underneath; also affects Pick color).
  `PositionPickerViewModel` (Pick position, "Use cursor position (3 s)" countdown) shared by Move / Click / Scroll;
  picking switches Click / Scroll to "at a chosen position". Shared XAML `PositionPickerTemplate` with screen-range text
  and an explanation of the coordinate system.
- Editors: Move = Destination + picker + accurate help (jumps, doesn't click). Click = Button, Action, Hold, "Where to
  click" (chosen position / current cursor / last found – same two flags underneath) + picker + help. Scroll = Direction
  (Up/Down/Left/Right = sign + Horizontal) + Number of notches + "Scroll position" + picker + "Exact amount (advanced)"
  raw Delta; stored values change only when edited (odd amounts like -60 are kept and explained). Tooltips on X/Y.
- Validation: a fixed position beyond ±100,000 px is now an **error**; off-screen stays a warning; both messages point
  to "Pick position". Zero scroll message says "Scroll at least 1 notch".
- Adding steps: toolbox "New steps go: After the selected step / At the end" (+ right-click a toolbox button: Add to end
  / Insert after selected). New step is selected and scrolled into view; status says where it went.
- Moving steps: `Core/Workflow/StepListEditing` (pure, tested): insert index, Move up / down / to top / to bottom, drop
  index; contiguous multi-selection moves as a group; Move to top/bottom and drag-and-drop take whole If / Repeat blocks
  along; a move that would turn valid blocks invalid is refused with a reason (allowed when blocks are already broken,
  so they can be fixed). Editor: Move to top / bottom buttons + Alt+Home / Alt+End, right-click menu on the list,
  drag-and-drop with a blue insertion line and edge auto-scroll, moved steps stay selected (`SelectStepsRequested`).
- Tests: `SmartMacro.Tests/EditorUxTests.cs` (insert positions, every move kind, drop positions, block safety/expansion,
  broken-structure fixing, objects/settings preserved, save/reload order; mouse playback incl. negative coordinates,
  click modes, scroll direction/amount/position, old scroll/click/move JSON, odd scroll amounts, coordinate validation).
- Docs: `docs/EDITOR_UX_MANUAL_TESTS.md`.
- Not verified: build, tests, the overlay on multi-monitor / mixed-DPI, drag-and-drop feel.

## Architecture
- `SmartMacro.Core` (net10.0, no WPF): models, validation, storage, logging. All unit tested.
- `SmartMacro` (net10.0-windows, WPF, MVVM): `Mvvm/`, `ViewModels/`, `Views/`, `Themes/`, `Services/`, `Converters/`.
- Recording: `Core/Recording/RecordingConverter` (desktop), `Core/Recording/GameRecordingConverter` + `IRelativeInputRecorder` (game), and `Core/Recording/SmartRecordingConverter` + `MouseContextClassifier` + `ISmartInputRecorder` (smart, the default). Model: `Core/Models/InputSequence.cs` (InputEvent, codec v1/u1) and `Core/Models/MouseSegment.cs`.
- `SmartMacro.Tests` (xUnit): 57 tests verified, plus about 35 new Phase 3 tests (engine, converter, validation) not yet run.
- Visual detection: `Core/Vision` (BgraImage, TemplateCodec, TemplateMatcher, ColorFinder, MarkerTracker, ZoneFinder, IScreenCapture) and `Services/Vision` (GdiScreenCapture, VisionTools + preview window + feedback measure / live preview).
- Feedback loops: `Core/Models/FeedbackLoopStep`, `Core/Playback/FeedbackSensor` (+ FeedbackStats), `Core/Playback/FeedbackController`, engine `RunFeedbackLoopAsync`.
- OCR: `Core/Ocr` (ScreenRect, IOcrService, OcrTextMatcher) and `Services/Ocr` (WindowsOcrService + ScreenCapture, FindTextTools + RegionPickerWindow). The app's TFM is now `net10.0-windows10.0.19041.0`, which is needed for the WinRT projections; Core and Tests stay on plain `net10.0`.
- Windows-only code is in `Services/Input` (NativeMethods, Win32InputSimulator, LowLevelInputHook / WindowsInputRecorder / EmergencyStopWatcher), `Services/HotkeyService.cs` and `Services/RunModeHost.cs` (banner + minimize).
- The composition root is in `App.xaml.cs`, with dependencies wired by hand (no DI container needed yet).
- The app is per-monitor DPI aware (`app.manifest`), so coordinates are physical pixels. It runs without admin rights (`asInvoker`).

## Verification
- `tools\verify.cmd` builds the solution and runs the tests. Output goes to `artifacts\build.log` and `artifacts\test.log`.
- **Latest run (2026-10-16, owner's PC, working copy = commit `91b67ff` + the uncommitted Editor UX changes):**
  build 0 warnings / 0 errors (`BUILD_EXIT=0`); tests **573 passed, 0 failed, 0 skipped** (`TEST_EXIT=0`).
  Includes Stage 4 (`FeedbackLoopTests`) and Editor UX (`EditorUxTests`). Unit tests use fake input / screen / clock:
  they don't prove that real apps or games react.
- Previous runs: Stage 4 first run 530/531 (one test bug, fixed in `01d0168`); Stage 3 389/389; Stage 2 317/318 then green.
- Older history: 57/57 (early phases).
- Manually verified on the owner's PC: app launch, create macro, add steps, validation blocking save, Delete key, Ctrl+S, key/modifier editing, close prompt, data kept after restart.
- The fix batch (command refresh, dark title bar, labels) built OK: 0 warnings, 57/57 tests.
- Phase 3 built and ran on the owner's PC. The "Wait between repeats" addition hasn't been built yet.

## Known issues / limits
- Windows doesn't let normal apps send input to, or record input from, apps running as administrator (UIPI). Nothing works on the lock screen or UAC prompts. Some games with anti-cheat ignore simulated input. SmartMacro doesn't try to bypass any of these.
- Coordinates are physical pixels. The app is per-monitor DPI aware. Recordings made with a different monitor layout may click in the wrong place.
- Pause takes effect between steps and inside delays (within ~50 ms). Held keys/buttons are released while paused and pressed again on resume.
- Game mode: games may still play a recording differently (frame rate, physics, network, server corrections, anti-cheat ignoring injected input). Mouse buttons 4/5 aren't recorded. Raw input from apps running as administrator isn't visible. "Enhance pointer precision" affects the visible cursor for injected relative moves the same way it does for a real mouse; games that use raw input aren't affected.
- The Speed field updates when you leave the box (Tab or click elsewhere), not on every keystroke.
- After the first click that closes a dropdown, the next click is ignored. This is normal WPF popup behavior.
- Message boxes use the standard light Windows style.
- OCR needs a Windows OCR language pack. Most Windows installs have one for the display language.
- Windows OCR has no confidence score. "Match accuracy" compares the text instead.
- OCR reads straight horizontal text best. Very small or stylized text and rotated text may not be read.
- If "Minimize while running" is turned off, OCR can read SmartMacro's own window.
- Find image matches only at the captured size (no scaling / rotation). Display scaling, game resolution or graphics
  quality changes need a new capture. Find color is colour matching only, not object recognition.
- GDI capture can return black for exclusive full-screen games or some overlays; windowed / borderless mode usually works.
- Full-screen image search costs tens of ms per check on a big screen (estimate; to be measured with STAGE2 test E).
- Feedback loops react a few frames late: capture + detection (a few ms for a small bar, to be measured) + up to one
  frame of waiting + the game's own render / input delay. They only compare colours; look-alike colours, flashing
  effects, zones with momentum and very fast markers are handled poorly or not at all. Mouse-button loops press where
  the cursor is. One input per loop. Stop conditions that use OCR pause the loop while they read. Default rate limits
  are guesses until the owner's measurements (STAGE4 tests B3 / D2) are in.

## Release preparation (not released; nothing published)
- Public documents live in `release-docs/` (EULA.txt, LICENSE.txt, THIRD-PARTY-NOTICES.txt, DOTNET-THIRD-PARTY-NOTICES.txt,
  README.txt). They are DRAFTS with [PLACEHOLDER] / [REVIEW] / [VERIFY] markers; not lawyer-reviewed, not legally cleared.
  Private notes (audit, checklist) stay in `Claude outputs/` and are never packaged.
- Dependencies read from the last publish's bundle manifest + local NuGet packages: .NET runtime 10.0.11 (MIT; single-file
  runtime parts under the .NET Library License), Windows Desktop/WPF 10.0.11 (MIT; PresentationNative_cor3 / wpfgfx_cor3 /
  vcruntime140_cor3 under the .NET Library License; D3DCompiler_47_cor3 under the Windows SDK license),
  Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 (Microsoft.Windows.SDK.NET.dll – Windows SDK license / redist list;
  WinRT.Runtime.dll 2.2.0.48161 – MIT). No other packages ship. Re-check after the next publish.
- `tools\publish.cmd` now packages only SmartMacro.exe + the five `release-docs` files, refuses unfinished documents
  (markers) unless `--allow-drafts` (then the ZIP is named ...-DRAFT-NOT-FOR-RELEASE), and stops if any other file appears.
- Attribution is not yet standardized in the app (AppInfo.Owner "Riku", "Owner & developer: Riku", sidebar "by Riku",
  csproj Company/Authors "Riku", Copyright "© 2026 Riku", README "© 2026 Riku") – change awaits owner approval.

## Next task
- Owner: manual re-test of the 8-bug fix batch (list above), then commit it together with the Editor UX changes.
- Owner: do `docs/STAGE4_MANUAL_TESTS.md` (start with `docs/feedback-test-page.html`) and send the 📏 numbers, then
  `docs/EDITOR_UX_MANUAL_TESTS.md`. Commit the Editor UX changes once they pass.
- Fix whatever the build / tests / manual tests show; set the default captures-per-second / min-change values from the
  measured numbers.
- Then the polish pass (Stage 2 / 3 improvements the owner mentioned + anything from Stage 4 testing), then `tools\publish.cmd`.
