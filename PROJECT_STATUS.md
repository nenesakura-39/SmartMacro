# SmartMacro – Project Status / Handoff

_Last updated: 2026-10-17 (Studio upgrades batch on top of `cdd0919` – failsafe release, Run step / Play from here, Undo / Redo +
copy / paste, target window + coordinate scaling, on-screen highlight, Humanize, Studio Library / Settings / Logs: written WITHOUT a
.NET SDK, NOT built or tested. Before it: Studio UI redesign on top of `d54c087` – new palette, pill switch / segmented controls,
175 px sidebar, unified editor header, bottom Activity + Problems drawer, 2-column toolbox tiles, step cards, inspector
header + pinned Test bar: written WITHOUT a .NET SDK, NOT built or tested. Before it: Next-gen QoL batch – "where to look" monitors / windows, smooth mouse glide, click timing,
freeze-screen picker, beginner Properties cards, feedback-loop wizard, live step highlight + status pill, Quick patterns:
written WITHOUT a .NET SDK, NOT built or tested yet. Before it: live-test fix batch (owner build 670/671, one test updated,
re-run pending); QoL batch; 8-bug fix batch 616/616.
Earlier: Stages 1–3 built, tested, manually tested by the owner. Stage 4 and Editor UX: 573/573, manual tests not done. Not released.
Developer: Riku (リク) · Brand: KuroLogic_

## Current status at a glance (2026-10-16)
| Area | Build + automated tests | Manual tests | Committed |
|---|---|---|---|
| Stages 1–3 | passed (Stage 3 run: 389/389) | done by owner (reported "Done"; details for Stage 3 not sent) | yes (`ef78ce4`) |
| Stage 4 – feedback loops | passed: 0 warnings, 0 errors, 573/573 | **not done** (`docs/STAGE4_MANUAL_TESTS.md`) | yes (`bb18e56`, test fix `01d0168`) |
| Editor UX – Pick position, mouse editors, add/move steps | passed in the same 573/573 run | **not done** (`docs/EDITOR_UX_MANUAL_TESTS.md`) | **no** – uncommitted changes on top of `91b67ff` |
| Release documents (EULA, LICENSE, notices, README) | – | – | drafts; `release-docs/` not committed yet |
| Release package | `publish\SmartMacro-1.0.0-win-x64\SmartMacro.exe` is an OLD build (made right after `159d6b3`, before Stage 1) | – | ignored by git |



## Studio upgrades batch (2026-10-17, on top of `cdd0919`) – 7 items; written WITHOUT a .NET SDK: **NOT built, NOT tested**
The AI's environment had no .NET SDK / NuGet / PyPI access (and no shell on the owner's PC), so nothing was compiled.
Checked by script: XAML well-formed, every `StaticResource` key defined, binding names exist, C# brace balance.
Next step for the owner: `tools\verify.cmd` (send `artifacts\build.log` / `artifacts\test.log` if anything fails),
then `docs/STUDIO_UPGRADES_MANUAL_TESTS.md`.
1. **Failsafe input guard**: `IInputSimulator.ReleaseAllHeldInputs()` (default interface method returning an error or null;
   fakes do nothing). New `Core/Playback/HeldInputTracker.cs`: `HeldInputTracker` (keys incl. scan-code keys + buttons, press
   order, thread-safe; release = buttons first, keys in reverse; every release tried) and `TrackingInputSimulator` decorator.
   `Win32InputSimulator` tracks every press (noted before sending) / release (after) and implements the failsafe (logs
   "Input safety: released …" only when something was left). `PlaybackEngine` `finally` calls it after its own
   `HeldInput.ReleaseAll` on complete / stop / error (failsafe errors become the result's Warning).
2. **Run step / Play from here**: `Core/Playback/PlaybackRanges.cs` (`SingleStep` = the step, or the whole If / Repeat block
   for a header / Else / End marker; `FromStep` = the step, or its innermost enclosing Repeat; Else → its "otherwise" part;
   `ValidateRange` = macro-wide + block + in-range errors only, plus "turned off / nothing enabled"). Engine
   `PlaySingleStepAsync` / `PlayFromStepAsync` (one pass, repeat count + repeat wait ignored; `RunContext.Start/End`; an End
   Repeat whose loop wasn't entered in a partial run just continues). `AutomationViewModel.RunSingleStepAsync /
   PlayFromStepAsync` (same overlay / Esc / validation dialog). Editor `RunStepCommand` (inspector header "▶ Run step",
   right-click, Ctrl+F5; a turned-off step is enabled in the played copy only) and `PlayFromHereCommand` (toolbar
   "⏭ Play from here", right-click). Wired in `MainViewModel` (`RunSingleStepHandler`, `PlayFromStepHandler`, `CanRunSteps`).
3. **Undo / Redo + copy / paste**: `Core/Workflow/UndoHistory.cs` (`UndoHistory` cap 50, dedupe, redo cleared by a new edit;
   `StepSnapshots` = step list ⇄ MacroJson). Editor records a snapshot before Add, Quick pattern, Delete, Duplicate, Move /
   drop, Wrap in If / Repeat, Clean up (only when it changed something), Paste; cleared on Load / Unload / Discard.
   `UndoCommand` Ctrl+Z, `RedoCommand` Ctrl+Y / Ctrl+Shift+Z (UserControl level; text boxes keep their own undo), ↶ / ↷
   toolbar buttons. `Core/Workflow/StepClipboard.cs`: clipboard text = `{format: "SmartMacro.Steps", schemaVersion,
   steps}`; paste gives fresh IDs, refuses newer formats and partial blocks; `ExpandSelection` widens a lone If / Repeat /
   End to its block. `CopySelectedStepCommand` Ctrl+C / `PasteStepCommand` Ctrl+V (step list + right-click), works across macros.
4. **Target window + resolution scaling**: `Macro.TargetWindowTitle`, `ReferenceLeft/Top/Width/Height`,
   `ScaleCoordinatesToWindow` (JSON: only written when set). `Core/Playback/CoordinateScaling.cs` (`CoordinateMapping`
   Map / MapRect; `CurrentTarget` = window by title via `SearchArea.FindWindow` or the primary monitor; `ApplyToCopy`
   maps Move / Click (+ drag end) / Scroll positions, custom search areas of Find steps + conditions, feedback bar areas;
   NOT recorded input, NOT template pictures). Engine plays a mapped copy (same step IDs) and fails with "The target
   window … isn't open" when missing. Validator: reference size required when scaling; title ≤ 260. Editor header
   "🎯 Any screen ▾" popover (editable window list + refresh, "Auto-adjust coordinates…" switch, reference text,
   "Use current size"; turning the switch on without a reference captures one). `ScreenInfoProvider` unchanged (already
   lists windows / monitors).
5. **On-screen highlight**: new `Services/Vision/ScreenHighlightOverlay.cs` (click-through, topmost, no-activate, tool
   window, excluded from capture, physical-pixel placement + DPI scale, cyan dashed search area, green box + crosshair or
   red box, label; holds 800 ms then fades 350 ms; one at a time; never throws). Shown by Test image / color / text /
   feedback "Test detection now" (`VisionTools`, `FindTextTools`) and by new "🎯 Show search area on screen"
   (`ScreenSearchStepViewModel.ShowSearchAreaCommand`, Feedback loop `ShowSearchAreaCommand`, buttons in
   `StepEditorTemplates.xaml`). `ScreenProbe` / `ConditionCheck` got an optional `Match` rectangle.
6. **Humanize**: `Macro.HumanizePlayback`, `ClickJitterPx?` (3), `DelayJitterPercent?` (8) (+ Effective… / Max… constants,
   validated 0–50). Engine `HumanizeRandom` (seedable), `Vary` (delays, repeat wait, glide + drag durations) and `Jitter`
   (Mouse click Click / Double / Hold at a position or the last found spot; Find / If "when found" clicks; clamped inside the
   matched rectangle when the spot is inside it). Recorded input and Down / Up / Move steps are never jittered. Editor header
   "Humanize" pill switch with tooltip.
7. **Studio UI**: `LibraryView` = search / filter card + Studio Macro Cards (title, description, step badge, category pills
   INPUT / VISION / OCR / FEEDBACK LOOP / LOGIC / HUMANIZE / 🎯 TARGET, "edited … ago", ▶ Play ✏ Edit 📋 Duplicate
   📤 Export .smacro 🗑 Delete per card) + "📥 Import .smacro" header button + details inspector. Library commands take the
   card as parameter (fallback: selection); search also matches category words. `MacroFileFormat.ExportExtension` =
   `.smacro` (`.smartmacro` still imported). `SettingsView` = About + Global Hotkeys / Recording Defaults / Playback &
   Safety (failsafe note, OCR status) / Storage & Data cards with `ToggleSwitchCheckBoxStyle`. `LogView` = #0D131F surface,
   level pills (`LogPillConverter`: INFO blue, MATCH violet, DONE green, WARN amber, ERROR red), search, All / Info / Match /
   Warn / Error filter (`LogViewModel.EntriesView`, `StringMatchConverter`), Copy Log (what's shown) / Clear Log.
- Files: `MacroDocument.CurrentSchemaVersion = 11`, written only when a macro-level option of item 4 / 6 is used.
- Tests: new `SmartMacro.Tests/StudioUpgradeTests.cs` (classes `ReleaseAllHeldInputsTests`, `PlayRangeTests`,
  `UndoRedoTests`, `CoordinateScalingTests`, `HumanizeTests`). No existing test was changed.
- Not verified: build, tests, anything at runtime (overlay on mixed DPI, popover focus, clipboard, Library card layout).
- Known gaps: Settings text "never saved" about OCR captures still contradicts the saved test PNGs (owner approval
  pending, unchanged); recorded-input positions are not scaled.

## Studio UI redesign (2026-10-17) – "SmartMacro Studio" look; written WITHOUT a .NET SDK: **NOT built, NOT tested**
The AI's environment had no .NET SDK / NuGet access (and WPF can't run on Linux), so nothing was compiled. XAML files were
checked for well-formed XML, duplicate keys, every `StaticResource` key and every EditorView / MainWindow binding name
against the C# sources. No ViewModel, Core or test file was changed. Next step for the owner: `tools\verify.cmd`
(send `artifacts\build.log` if XAML compile errors show up), then `docs/STUDIO_UI_MANUAL_TESTS.md`.
Files: `Themes/Colors.xaml`, `Themes/Controls.xaml`, `MainWindow.xaml` (+ caption colour in `.xaml.cs`), `Views/EditorView.xaml`,
`Views/StepEditorTemplates.xaml`, `Converters/Converters.cs`, new `docs/STUDIO_UI_MANUAL_TESTS.md`.
1. **Palette** (old key names kept, values changed app-wide): canvas #0B0F17, cards #111827 / border #1F2937, inputs #1E293B /
   border `InputBorderBrush` #334155 (blue on focus), muted text #94A3B8, success now #10B981. Category brushes
   `{Input|Timing|Vision|Logic|Feedback|Recorded}{Accent|Badge|Soft}Brush`; `StepsCanvasBrush`, `StepCardBrush`,
   `SelectedCardBrush`, `RunningCardBrush`, `SectionCardBrush`, Record / Play brushes.
2. **New styles** (`Controls.xaml`): `ToggleSwitchCheckBoxStyle` (38×20 pill), `SegmentedContainer` + `SegmentedRadioButtonStyle`,
   `ToolboxTile` (52 px), `ToolboxCategoryHeader`, `StepCardItem` (invisible ListBoxItem; card draws states), `Pill`,
   `RecordButton`, `PlayButton` (blue → green on hover), `TestActionButton` (violet), `InspectorActionBar`, `GhostToggle`,
   `HeaderNumberBox`. `SetupCard` = #151E2E, radius 8, padding 12, margin 0,0,0,10; `SetupCardTitle` = 11 px #94A3B8 SemiBold
   (texts written in capitals; VM-provided titles go through `UpperCase`). NavButton = compact pill.
3. **MainWindow**: sidebar 175 px with a collapse toggle (60 px icon rail; `BoolToGridLengthConverter`), compact footer
   badge (version, owner, "Esc stop · rec · play" hotkeys; tooltip = EmergencyHint). The shared automation bar and
   Activity panel are hidden on the Editor page only (style DataTriggers on `CurrentPage`), still shown elsewhere.
4. **EditorView**: one header card (name / description | saved pill, Discard, Save | ● Record, ▶ Play, Pause / Stop bound
   to `MainViewModel.Automation` via the Window | Play n times, Speed ×, Wait ms, "Mini overlay" / "Minimize" pill
   switches). Bottom drawer: 32 px bar (state pill, problems badge = toggle, latest log line or ProgressText while busy,
   Open full log, Expand ▴) + 120 px Activity log | GridSplitter | Problems. Toolbox: Insert segmented pill
   (`AddAfterSelected` / `AddAtEnd`), INPUT / VISION & AI (`StepTypeFilterConverter` on `StepTypes`), LOGIC & LOOPS
   (`LogicStepTypes`) as 2-column tiles, QUICK PATTERNS as full-width rows. Steps: dot-grid canvas #0D131F, cards #161F30
   with 3 px category strip + 32×32 badge, selected = blue border + glow, `IsRunning` = emerald + "▶ RUNNING", disabled =
   0.45 + DISABLED pill, indentation 24 px / level with guide lines (`IndentLevelConverter` on `IndentLevel`; VM's
   22 px `IndentMargin` no longer used), inline previews via `StepKindTemplateSelector` (Find image 28×28 thumbnail,
   Find color / Feedback 16×16 swatches). Properties: sticky header (badge, title, Step N pill, Enabled switch) and a pinned
   bottom bar "Test detection now" + result (`TestCommand` for Find steps, `TestDetectionCommand` for Feedback loop).
   Names `StepList`, `PropertiesScroll` and all code-behind handlers unchanged.
5. **StepEditorTemplates**: `StepHeaderTemplate` = description + problem banners only (title / Enabled moved to the
   inspector header). In-flow Test buttons + results only show for editors embedded in conditions (`IsStandalone` false);
   Feedback "Test detection now" and its result box moved to the pinned bar (Measure / Preview stay in the card).
- Not verified: XAML compile, runtime look, DPI, drawer / sidebar behaviour, ElementName binding on the sidebar
  ColumnDefinition, glow performance on long lists.

## Next-gen QoL batch (2026-10-17) – 7 upgrades; written WITHOUT a .NET SDK: **NOT built, NOT tested**
The AI's environment had no .NET SDK and no NuGet / package access, so nothing was compiled. XAML files were checked for
well-formed XML and every `StaticResource` key / step-editor binding path was checked by script against the C# sources.
Next step for the owner: `tools\verify.cmd` (expect a few compile fixes), send the logs, then `docs/NEXTGEN_QOL_MANUAL_TESTS.md`.
1. **"Where to look"** (`Core/Vision/ScreenLayout.cs`: `SearchScope` AllMonitors / PrimaryMonitor / Monitor / Window /
   CustomArea, `MonitorInfo`, `WindowInfo`, `IScreenLayout`, `SearchArea.Resolve / FindWindow / NumberMonitors / Describe`):
   - `ScreenSearchStep.Scope` (null in old files → `EffectiveScope` = AllMonitors when `SearchFullScreen`, else CustomArea),
     `MonitorNumber` (1 = main, then left→right, top→bottom), `WindowTitle` (exact title wins, else "contains", minimized
     only as last resort; DWM extended frame bounds, clipped to the desktop). `SearchFullScreen` is still written and kept
     in sync (true for every scope except CustomArea) so older builds read files sensibly.
   - `ScreenProbes` resolves the area on EVERY look (windows move); an unavailable monitor / window is a miss with a plain
     message ("The window "Roblox" isn't open."), so the step keeps waiting until its timeout. Without a layout the main
     monitor = whole desktop (tests / fallback). Engine ctor got optional `IScreenLayout layout` (5th parameter).
   - App: `Services/ScreenInfoProvider` implements `IScreenLayout` (EnumDisplayMonitors + MONITORINFOEX primary flag,
     EnumWindows: visible, titled, not cloaked / tool / owned, not SmartMacro's own process; physical-pixel scope).
     One instance is shared by the engine, editor, VisionTools and FindTextTools ("Test detection now" uses the scope).
   - New toolbox steps and new conditions (`MacroFactory.NewCondition`) use **Main Monitor**; `new FindImageStep()` in code
     keeps the old whole-screen default. Validator: unknown scope, monitor 1–16, window title required (≤ 260). Colour
     warning now for "whole monitor or more". Move-away after click: custom area / window → outside its bottom-right
     corner; monitors → 150 px below the match.
2. **Mouse movement + click timing**: `MouseMoveStyle` (Instant / SmoothGlide / HumanCurve) + `MoveDurationMs` (120) on
   Move mouse, Mouse click and Find steps (null = Instant = old behaviour; recordings stay instant). Planner
   `Core/Playback/MouseMotion.Plan` (8 ms ticks; glide = straight line + smoothstep; curve = cubic Bézier bending ≤ 12 % /
   60 px, smootherstep, deterministic side; last point exactly on target). Engine `MoveCursorAsync`: Move / Click steps
   glide on the timeline (÷ Speed), Find "when found" / move-away glide in real time; unknown start → jump. The old
   Settings "smooth mouse" pre-glide skips steps that have their own style. Click timing: Find steps `ClickHoldMs` 35,
   `DoubleClickIntervalMs` 65, `PostActionDelayMs` 120 (same as the old fixed constants, which now point at
   `MouseMotionDefaults`); Mouse click: `HoldMs` (= `ClickHoldMs` alias), `DoubleClickIntervalMs`, `PostActionDelayMs`
   (0 in old files / recordings; new steps 35 / 65 / 120). Timed double click when hold or gap > 0. "Instant" still gets
   the existing Win32 hover nudge before a press.
3. **Freeze-screen picker** (`Services/Ocr/RegionPicker.cs`, moved out of FindTextTools.cs; `Services/Vision/ScreenFreezeOptions`):
   every Pick area / Capture image / Pick color / Pick position / feedback pick takes one GDI screenshot of the desktop and
   shows it per monitor (banner "Screen Frozen — … [Space] … [Esc]"), optional 3 / 5 s countdown (click-through,
   capture-excluded banner; Esc cancels), Space = hide + (countdown) + new frame, 8× magnifier (15×15 px, grid, centre
   box, `#RRGGBB RGB(…)`, X / Y). Capture image / Pick color take their pixels from the frozen frame (`PickerResult`).
   "Freeze:" drop-down next to the pick buttons (one shared setting, not saved).
4. **Properties panel**: Find text / image / color = summary banner (`StepDescriber.Explain`) + cards 1. What to find /
   2. Where to look / 3. What to do when found / 4. Search timing + collapsed `AdvancedExpander` "Advanced Tuning &
   Movement" (accuracy, edge / see-through options, movement style, glide, click hold, wait after click, double-click
   gap, move away, offsets). Mouse move / click got movement + timing fields. Tooltips on labels and controls. New styles
   in `Themes/Controls.xaml`: `SetupCard`, `SetupCardTitle`, `SummaryBanner`, `AdvancedExpander`.
5. **Feedback loop wizard**: Step 1 preset cards (`MinigameCard` / `MinigameKind`: Horizontal Bar Follower, Vertical Bar
   Follower, Timing / Skill Check, Custom) → Step 2 bar area → Step 3 target colour + tolerance slider → Step 4 your bar /
   zone colour + slider; rule preview card (`StepDescriber.ExplainFeedbackRules`). Old preset drop-down replaced.
6. **Live highlight + status pill**: `AutomationViewModel.RunningStepChanged` → `EditorViewModel.SetRunningStep` →
   `StepViewModel.IsRunning` (green "▶ Running" badge + left border, list scrolls to it; matched by step ID so it works on
   the played copy). Pill = the existing click-through `RunOverlayWindow`, now compact in the top-right corner with
   `SmartMacro: Step 2/5 — Finding image "SHAKE" on the main monitor… (Esc to stop)` (`StepDescriber.Activity`, a
   step's comment is its name). Toolbar toggles "Show mini status overlay while playing" / "Minimize main window while
   playing" save straight to settings (`SettingsViewModel.QuickSetAsync`).
7. **Quick patterns** (`Core/Workflow/QuickPatterns.cs`, toolbox section, `EditorViewModel.AddPatternCommand`):
   Auto-Click Dynamic Button Loop (Repeat While image, 800 ms grace, 60 s limit → Find image click + glide + 120 ms wait +
   move away → End Repeat), Fishing Cast + Shake + Reel (Hold 800 ms → Delay 1 s → Shake loop → Feedback loop horizontal
   follower), Shop / Menu Click & Buy (Find image click → scroll 3 notches → Mouse click). Comments say what to capture.
- Files: `MacroDocument.CurrentSchemaVersion = 10`, written only for main monitor / monitor / window scopes, a glide
  style, or click timing that differs from the old values. Old files load unchanged.
- Tests: `SmartMacro.Tests/QolTests.cs` new classes `SearchScopeTests`, `MouseMotionTests`, `ClickTimingTests`,
  `QuickPatternTests`, `PlainEnglishTests` (+ `FakeLayout`). Updated on purpose: `LiveTestFixTests` format test
  (`CurrentSchemaVersion >= 9`). Hold-time validator warning no longer fires for Double click or the 35 ms default.
- Not verified: anything on Windows (monitor / window enumeration, DWM bounds, frozen overlay on mixed DPI, magnifier
  speed, countdown focus behaviour in Roblox, glide feel), and the XAML at runtime.

## Live-test fix batch (2026-10-17) – 7 bugs from the owner's live test of `b408b36`; written WITHOUT a .NET SDK: **NOT built, NOT tested**
The AI's environment had no .NET SDK (and no package / NuGet access), so nothing was compiled. Next step for the owner:
`tools\verify.cmd`, send the logs if anything fails, then `docs/LIVE_TEST_FIXES_MANUAL_TESTS.md`.
The matcher maths for bug 4 was checked with a NumPy port of the new C# code (same thresholds) on the test scenes:
see-through button over water → normal search rejected (colour), foreground search 100 % at the right spot; plain water
→ best 0 %; yellow strokes → rejected unless "Ignore color"; thin "Hi" on a 1424×777 area → exact at scale 1/2.
1. **Windowed layout** (`MainWindow.xaml`, `Views/EditorView.xaml`): window 1340×860 (min 1024×680); editor header is
   two compact rows; workspace row `*` with MinHeight 360; Problems is a one-line banner (list only with problems, max
   84 px); toolbox "New steps go" radio pair → one checkbox "Add new steps at the end" (bound to `AddAtEnd`);
   GridSplitters (style `PanelSplitter` in `Themes/Controls.xaml`) between Add step | Workflow steps | Properties
   (200 / * / 360 px, min/max limits); Activity list 72 px (~110 px panel) with a drag grip (`Thumb`,
   `MainWindow.ActivityGrip_DragDelta`, 40–420 px). Margins of page / automation bar reduced.
2. **Properties text** (`Themes/Controls.xaml`, `Views/StepEditorTemplates.xaml`): CheckBox / RadioButton templates use a
   Grid + wrapping TextBlock (long labels wrap); `FieldLabel` wraps; 2-column grids that held drop-downs or long labels
   are stacked full-width (Mouse click Button/Action, Scroll Direction/Notches, condition Look for/True when it,
   feedback marker tolerance row, Captures/Min change, Max runtime/When time is up, Target missing/Then); Properties
   header has its own bar, ScrollViewer padding 16,10,16,28. No fixed heights were found on the condition card.
3. **Wheel over drop-downs** (`Views/EditorView.xaml.cs`): `Properties_PreviewMouseWheel` on the Properties ScrollViewer:
   wheel over a closed ComboBox or a Slider is handled and re-raised as MouseWheel on the ScrollViewer; open drop-downs
   scroll their list.
4. **Find image** (`Core/Vision/TemplateMatcher.cs`, new `Core/Vision/ForegroundMatcher.cs`):
   - Templates < 120 px on their short side never use a 1/4 (or smaller) coarse pass unless 1/2 would cost > 8× the
     normal budget (320 M multiply-adds; e.g. a 103×85 template on a full 1920×1080 screen still uses 1/4 for speed –
     a picked search area avoids that). When a small template still goes to 1/4, 3× more candidates are refined.
   - New `TemplateMatchOptions.MatchForeground` / `FindImageStep.MatchForeground` (checkbox "Ignore background / match
     bright text and edges (for see-through game UI)", also inside image conditions): Otsu threshold on the template;
     the side (bright / dark) farthest from the template's border is the foreground (needs 2–75 % of pixels, ≥ 25 grey
     levels contrast, self-score ≥ 0.6). Score = correlation between the foreground mask and the screen's grey levels,
     signed, divided by the template's own score (template = 100 %). Coarse pass (1/2 or 1/4 by a 60 M budget) + exact
     refine; colour check compares the foreground pixels' average colour only. Each spot gets the better of the normal
     and the foreground score. Miss texts suggest the option.
5. **Fisch SHAKE loop** (`Core/Playback/PlaybackEngine.cs`, `Services/Input/Win32InputSimulator.cs`):
   - `RepeatStep.KeepCheckingForMs` (null = automatic: 800 ms for image / colour conditions, 0 for text; editor field
     "Keep checking before leaving (ms)", 0–60,000): only when the loop would end because the target is NOT seen
     (While "is on screen", Until "is NOT on screen") the condition is re-checked (every "check every") for up to that long
     and the loop continues as soon as the target is back. Seeing the target is trusted at once. Log says
     "(kept checking for 800 ms)".
   - "When found" clicks (Find steps and If "when true"): button held `FoundClickHoldMs` 35 ms (tracked; Stop releases),
     double-click gap `DoubleClickGapMs` 65 ms, post-action delay `ClickSettleMs` 100 → 120 ms.
   - New `ScreenSearchStep.MoveCursorAwayAfterAction` (checkbox under the offsets): after the click / hold the cursor
     moves just outside the search area's bottom-right corner (+8 px), or 150 px below the match for whole-screen searches.
   - Win32: every press after an absolute cursor move gets a relative hover nudge (+2,+2 then −2,−2 over ~15 ms) and an
     exact absolute move back + 20 ms settle (releases after a drag get the old 1 px absolute nudge, no wiggle); such
     "clicks on a jumped-to spot" keep the button down ≥ 35 ms and a second press within 65 ms (double-click) waits for
     the gap. Feedback loops / recorded game input pressing in place are not affected.
6. **Smooth playback** (`PlaybackEngine`, `Core/Recording/MousePathSimplifier.cs`, Settings):
   - `PlaybackEngine.SmoothMouseMoves` (app: `AppSettings.SmoothMouseMoves`, default on, checkbox in Settings; off in
     the engine by default so old tests keep exact calls): a Delay followed by a Move / Click-at-position / Scroll-at-
     position uses its last ≤ 200 ms to glide there (8 ms steps, ease in/out) from the last absolute position the engine
     set (or the real cursor). Total time unchanged. Unknown start → no glide.
   - Drags: `DragStepInterval` 16 → 8 ms (~120 Hz).
   - Simplifier: while a button is held, tolerance ≤ 1 px and a point at least every 50 ms (reversals kept as before);
     free movement unchanged.
7. **Test detection hides SmartMacro** (`Services/Vision/VisionTools.cs`, `NativeMethods.MakeInvisible`): Test image /
   Test color / feedback "Test detection now" make SmartMacro and its owned windows layered alpha 0 + excluded from
   capture for 150 ms (`TestHideMs`), capture, then restore before the result window opens (no minimize, focus stays).
   Pick / Capture / Measure / Preview / Track still minimize as before.
- Files: `MacroDocument.CurrentSchemaVersion = 9`, written only for MatchForeground (step or image condition),
  MoveCursorAwayAfterAction, or an explicit KeepCheckingForMs. Old files load unchanged (automatic grace applies to
  image / colour While loops – that IS a behaviour change for old macros, intended).
- Tests: new `SmartMacro.Tests/LiveTestFixTests.cs` (scale choice, thin text, see-through button normal vs foreground,
  plain water, foreground colour check, patterned icon, Find image click over water, Repeat grace while / until / 0 /
  timing, validation, found-click hold + double-click gap timing, move away (full screen / area), format 9 round trip +
  old files, smooth glide timing / off / unknown start / click, drag simplifier density). Updated on purpose:
  `QolTests.Drag_glides…` (8 ms steps → 20 moves), `QolTests` format test (CurrentSchemaVersion ≥ 8),
  `BugFixTests.Slider_drag…` (drag keeps 4–40 points instead of ≤ 4).
- Not verified: build, tests, any of it in Roblox / Windows (layered-window hiding, wheel re-routing, GridSplitter feel).

## QoL batch (2026-10-17) – written by the AI WITHOUT a .NET SDK: **NOT built, NOT tested yet**
The AI's environment had no .NET SDK and no package access, so nothing below was compiled. Next step for the owner:
`tools\verify.cmd`, send the build / test log if anything fails, then `docs/QOL_MANUAL_TESTS.md`.
Audit first: several asked-for parts already existed (Find steps already had click / move / double / right-click at the
center + offset and Stop / Skip next / Continue; Repeat already had Times / While / Until with conditions and limits;
Find text / image / color already had "Test now"; `MousePathSimplifier` already thinned paths). The batch adds what was missing:
1. **When found / when true**: new `FoundTextAction.Hold` + `ScreenSearchStep.ActionHoldMs` (hold the left button on the
   match). `IfStep.ThenAction / ThenOffsetX / ThenOffsetY / ThenHoldMs`: when the condition SAW its target, act on that
   spot before the inner steps (never for "is NOT on screen"; validator warns). Engine: shared `PerformFoundActionAsync`
   (hold is tracked in HeldInput; click / hold settle 100 ms). Describer: `Find image (121×134) on screen → Click center
   when found (wait up to 10 s; if not found: skip next step)`, `If … → Click center when true`. Editor: "When found" /
   "When not found" drop-downs reworded, hold-time box, If "When true" section.
2. **Repeat while / until**: already there (Stage 3). Only labels / help text improved (example: Repeat UNTIL reeling bar
   is on screen with Find "Shake" → Click its center inside).
3. **Test detection now**: buttons renamed; results now read `Matched at (x, y) — 94% confidence` / `Matched! Read text in
   area: 'OK'` / `Not found. Text read in area: '0K'`; **Show me** moves the cursor onto the match (`LastTestMatch`,
   `ShowSpot` on IVisionTools / IFindTextTools); conditions show `Right now this condition is TRUE / FALSE`; Feedback loop
   got `TestFeedbackOnceAsync` (one capture: marker %, zone, region, what it would do, overlay picture, no input).
4. **Feedback presets**: `FeedbackPreset` (Custom default, not written to files when Custom) + `Core/Models/FeedbackPresets`.
   FollowTargetBar = zone found by colour (your bar), marker = target; horizontal: before Release / inside **Pulse** /
   after Hold (vertical swapped), lost 1.5 s → exit loop, 60 s max. New `FeedbackAction.Pulse` (inside only; duty =
   how far the marker is toward the "hold" edge, snapped to 0 / 1 near the edges, `PulsePeriodMs` 200) in
   `FeedbackController.PulseDuty`. ClickWhenInZone = before / after Release, inside Tap (or Hold / Release / Hold for the
   "release the held input" variant), dead band 0, new `FinishWhenInZone` (engine does the inside rule once, then ends
   "finished: the marker reached the zone"). Editor: preset selector, guided "1. Pick minigame area / 2. Pick marker /
   3. Pick zone" panel, "Show all settings (advanced)". Marker detection is still colour only (no image marker).
5. **Mouse click Hold / Drag**: `MouseClickAction.Hold` (HoldMs, required 1–60,000; default 850 when chosen) and
   `MouseClickAction.Drag` (`EndX / EndY / DragDurationMs` 500; 16 ms steps on the timeline, Speed applies, end exactly
   on target; from "cursor" uses new default-interface `IInputSimulator.TryGetCursorPosition`, Win32 implements it).
   Editor: "Action type" drop-down with labels, end-position picker (`PointEditorViewModel`).
6. **Simplified recordings**: `RecordingOptions.CollapseMousePaths` / `SmartRecordingOptions.CollapseMousePaths`
   (Core default off) drop cursor travel while no button is held (clicks / wheel carry their position; one Move is kept
   before a key event; drags untouched). Setting `AppSettings.SimplifyMousePaths` (default on, also for old settings
   files) + checkbox in Settings. **Clean up mouse paths** command (toolbar + right-click) = `Core/Workflow/StepCleanup`:
   runs of enabled, comment-less Delay / Move steps → Delay (+ Move only if needed) (+ Delay), same total time; drags,
   commented and disabled steps untouched; selection-only when several steps are selected.
- Files: `MacroDocument.CurrentSchemaVersion = 8`, written only when one of the new options is used.
- Tests: new `SmartMacro.Tests/QolTests.cs` (found / when-true actions incl. negated, hold timing, moving target;
  click hold / drag timing + unknown cursor + stop releases; pulse duty / cycle, presets, finish-in-zone tap / release /
  already-inside; validation; describer; format 8 round trip + old files + preset not written; 5 clicks → 9 steps with
  timing kept, drags kept, hover-before-key; StepCleanup cases). Updated on purpose: `VisionTests.Describes_visual_steps`
  (new "Find … → … when found" wording).

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
- **2026-10-17 16:48 fresh publish (owner's PC, `tools\publish.cmd`, real build, no --allow-drafts):** tests
  **709/709 passed**; publish OK; all five docs passed the marker check; `bundle-versions.txt` OK (every bundled component
  listed: .NET / WPF runtime packs 10.0.11, Windows SDK projection 10.0.19041.57). Output:
  `release\SmartMacro-1.0.0-win-x64.zip` (62.2 MB; SmartMacro.exe 68.1 MB + 5 docs), SHA-256
  `87D34104353DBF605F84B90BB7F27C375C8E0B2B0B60F3CB41FEE42874AA1CFF`. Built from the current working copy (includes the
  next-gen QoL + live-test fix batches, which therefore compile). Sent to a friend as an early test build for debugging;
  manual tests still open. Version stays 1.0.0 – bump `<Version>` in the csproj (and EULA/README) for the next test build.
- **2026-10-17 EULA simplification (docs only, no code changed):** `release-docs/EULA.txt` rewritten as a shorter
  plain-English draft (old text: `Claude outputs/archive/EULA-long-draft-2026-10-16.txt`). Privacy section written from the
  source (no network code; local data folder, daily logs, last test PNGs, diagnostics on click only). README version set to
  1.0.0; two [VERIFY] items added to THIRD-PARTY-NOTICES. Still open: licensor name, contact, commercial use, liability and
  governing-law decisions, download link, third-party [VERIFY] items, fresh publish, manual tests. Details and the full
  marker list: `Claude outputs/EULA_AND_RELEASE_REVIEW_2026-10-17.md`. App UI text in `Views/SettingsView.xaml` ("never saved")
  contradicts the saved test PNGs – needs a wording fix before release (awaiting owner approval).
- The package in `publish\` (DRAFT, EXE 2026-10-16 23:04) predates the 8-bug fix and QoL batches – do not use it.
- **Owner decisions (2026-10-17):** licensor Riku (リク) (individual, KuroLogic = unregistered brand), any use incl.
  commercial, contact via the KuroLogic Discord server, distribution via Discord + Google Drive, early test release for
  feedback. EULA / LICENSE / README now have no markers. THIRD-PARTY-NOTICES still needs the WPF notices pasted by hand
  and a version re-check after the fresh publish.
- `tools\publish.cmd` updated (2026-10-17): version read from `SmartMacro.csproj`, EULA/README must mention that
  version, a real build removes the old DRAFT package of the same version, writes `release\bundle-versions.txt` (via new
  `tools\bundle-versions.ps1`) and a SHA-256 file next to the ZIP. README/EULA now carry the real YouTube, Discord and
  Ko-fi links. Written without a Windows machine – first run on the owner's PC still to do.
- Later the same day: THIRD-PARTY-NOTICES finished (WPF's zlib 1.2.11 + Json.NET notices added as section C; DRAFT
  header and UNRESOLVED list removed). `bundle-versions.ps1` now checks every bundled file name + version against the
  notices and `publish.cmd` stops a real release on a mismatch. All five public documents now pass the marker check.
  Icon `Assets/SmartMacro.ico` (AI-made blue square + lightning bolt) kept as is. Cleanup of old drafts / old DRAFT
  package left to the owner (this session can't delete files).
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
- Owner: `tools\verify.cmd` for the Studio upgrades batch (never compiled; send `artifacts\build.log` /
  `artifacts\test.log`), then `docs/STUDIO_UPGRADES_MANUAL_TESTS.md`.
- Owner: `tools\verify.cmd` for the Studio UI redesign (XAML never compiled; send `artifacts\build.log` if it fails),
  then `docs/STUDIO_UI_MANUAL_TESTS.md`.
- Owner: `tools\verify.cmd` for the next-gen QoL batch (never compiled; expect a few compile fixes – send
  `artifacts\build.log` / `artifacts\test.log`), then `docs/NEXTGEN_QOL_MANUAL_TESTS.md`.
- Owner: `tools\verify.cmd` for the live-test fix batch (never compiled; expect a few compile fixes), then
  `docs/LIVE_TEST_FIXES_MANUAL_TESTS.md` (send the 📏 SHAKE confidence numbers over wood / water).
- Owner: run `tools\verify.cmd` for the QoL batch (never compiled yet; expect a few compile fixes), then
  `docs/QOL_MANUAL_TESTS.md`.
- Owner: manual re-test of the 8-bug fix batch (list above), then commit it together with the Editor UX changes.
- Owner: do `docs/STAGE4_MANUAL_TESTS.md` (start with `docs/feedback-test-page.html`) and send the 📏 numbers, then
  `docs/EDITOR_UX_MANUAL_TESTS.md`. Commit the Editor UX changes once they pass.
- Fix whatever the build / tests / manual tests show; set the default captures-per-second / min-change values from the
  measured numbers.
- Then the polish pass (Stage 2 / 3 improvements the owner mentioned + anything from Stage 4 testing), then `tools\publish.cmd`.
