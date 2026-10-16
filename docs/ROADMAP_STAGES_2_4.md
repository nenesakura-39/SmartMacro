# Roadmap – Stages 2, 3 and 4 (proposal, not implemented)

Each stage starts only after the previous one has passed its automated **and** manual tests.
Nothing here is game-specific: Fisch, Grow a Garden, etc. are examples a user would *configure*, never code paths.

## Shared rules for all stages

- Windows-only code (screen capture, input) stays in the WPF project under `Services/`; models, logic, validation
  and timing go in `SmartMacro.Core` behind interfaces so they can be unit tested with fakes.
- Every new step type: a sealed `MacroStep` subclass with a JSON discriminator, validation rules in `MacroValidator`,
  a step view model + template, and a schema-version bump only when files need it (old files keep loading).
- Nothing leaves the PC. No arbitrary code/scripts in macro files. No injection, drivers or anti-cheat bypasses.
- Detection/loops never run on the UI thread, always take a `CancellationToken`, and respect Esc / Stop / Pause.

---

## Stage 2 – Reusable visual detection

**2.1 Screen capture service (foundation)**
- `Core/Vision/IScreenCapture` → `CaptureAsync(ScreenRect region, CancellationToken)` returning a small `BgraImage`
  (width, height, stride, `byte[]`). Real version reuses the existing GDI BitBlt code from `Services/Ocr/ScreenCapture`;
  later optionally Windows.Graphics.Capture for games where BitBlt returns black.
- SmartMacro's own windows stay excluded (`WDA_EXCLUDEFROMCAPTURE`), coordinates stay physical pixels.
- Measure: capture time per region size, logged by the Test tool.

**2.2 Find image step**
- `FindImageStep`: template image (stored next to the macro as a PNG, referenced by file name + SHA-256 so a
  changed/missing template is detected), region or full screen, similarity threshold (e.g. 0.80–1.00), timeout,
  check interval, "find all" with a max count, action (none / move / click / double / right) + offset, not-found behaviour.
- Matching in Core: normalized cross-correlation on grayscale, coarse-to-fine (downscaled search, then refine).
  Pure C# first (no dependency). If it's too slow on full screens, evaluate **OpenCvSharp4** (Apache-2.0, native
  binaries ~60 MB) and only adopt it with measured numbers.
- Result model `VisualMatch(ScreenRect Bounds, double Score)`, shared with OCR's `TextMatch` through a small
  `IDetectionResult` so later steps can use "last found" for both.
- Editor tools: **Capture template** (drag a rectangle, like Pick area), **Pick search area**, **Test now** showing the
  captured area with the match rectangle and score, saved to `Logs\last-image-test.png`.
- Limits documented: scale / DPI / graphics-quality changes break templates; transparent/animated UI needs a lower threshold.

**2.3 Colour and moving-target detection**
- `FindColorStep`: colour (picked from screen), tolerance (per-channel or ΔE), region, minimum blob size; returns the
  blob bounds and centre. Explicitly *not* object recognition.
- `Core/Vision/BarTracker`: inside a user-selected horizontal (or vertical) region, finds the marker by colour or
  template and returns its position as 0–100 % along the bar, plus a confidence and "ambiguous" flag (two candidates,
  too few pixels). Tracks between frames with a max-jump limit so a one-frame glitch is ignored.
- Test tool shows a live preview (a few fps) of what the tracker sees.

**2.4 OCR integration**
- Keep `FindTextStep` as is. Add "Wait until the text disappears" and expose the found-text result to the condition
  model from Stage 3. Image and text stay separate steps.

**2.5 Acceptance (from the brief)** – regions and full screen; correct physical-pixel coordinates on multi-monitor /
mixed DPI; test without playing a macro; clear timeout / no-match messages; CPU and capture cost measured and logged;
UI stays responsive; existing OCR macros unchanged.

---

## Stage 3 – Conditional workflows and loops

**Model: blocks, not scripts.** Steps stay a flat list for the existing editor, with explicit block markers:
`IfStep` / `ElseStep` / `EndIfStep`, `RepeatWhileStep` / `RepeatUntilStep` / `EndRepeatStep`, `WaitUntilStep`,
`WaitForChangeStep`. The validator checks that blocks are balanced and nested correctly before saving or playing.

**Conditions** are data: `Condition { Kind = TextVisible | ImageVisible | ColorVisible | MarkerInZone | LastFound…,
Settings…, Negate }`, evaluated by an `IConditionEvaluator` that reuses Stage 2 detectors. No expressions, no code.

**Safety limits (required fields, validated):** every loop has a max iteration count *and/or* max duration; every wait
has a timeout and a "when it times out" choice (stop with error / continue / jump to Else). Nesting depth limit (e.g. 8).

**Engine:** `PlaybackEngine` gets a small program counter instead of `foreach`, so blocks can jump. Pause / Stop /
Esc are checked inside every condition check and loop turn; held keys/buttons are released on cancel exactly like now.

**Editor:** indentation for blocks, "Add If/Else", "Wrap selection in Repeat", Problems list shows unbalanced blocks.

**Tests:** success, false branch, timeout, max iterations, cancellation inside a loop, pause/resume inside a wait,
corrupted/unbalanced files, deeply nested blocks.

---

## Stage 4 – Continuous visual feedback

A **Feedback loop** step built from Stage 2 + 3 parts, configured by the user:

- *Observe:* a region + tracker (e.g. marker position 0–100 %) and an optional target zone (detected or fixed %).
- *Decide:* a few rules, e.g. "if marker < zone start → hold Left mouse; if marker > zone end → release", with a
  dead-band (hysteresis) to avoid rapid toggling.
- *Act:* hold / release / tap a key or button (only these, tracked so they're always released).
- *Stop when:* completion condition (text/image/colour visible), failure condition, target lost for N ms, or max runtime.
- *Rate limits:* capture rate (e.g. 30–60 Hz max), minimum time between input changes.

**Measured, not assumed:** the step logs capture time, detection time, achieved loop rate and input-change count, and the
editor's Test mode shows them. Latency (capture + detect + input) is reported so users know what's possible.

**Fails safely:** marker lost → configurable (release and wait / stop); ambiguity → no action that frame; Esc and Stop
always work; everything held is released on stop, failure, timeout and app exit.

**Honest scope:** this can handle simple "keep the marker in the zone" minigames that the user configures; it will not
handle every minigame, and it never reads game memory or bypasses anti-cheat.
