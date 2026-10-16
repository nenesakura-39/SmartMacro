# Stage 4 – Manual tests (Feedback loop: continuous visual feedback)

Do these after `tools\verify.cmd` is green. Each test says what to do and what you should see.
Tick a box when it works; if something doesn't, write down the test number, what you did and what happened
(a screenshot of the editor, the preview window and the Logs page helps a lot).

You need: SmartMacro, a web browser, and the test page **`docs\feedback-test-page.html`** (double-click it; it works
offline). Start with the test page, **not** a game: the page shows exactly what SmartMacro presses, so you can tell a
SmartMacro problem from a game problem.

Tip: keep the **Logs** page open. Each feedback loop writes **one** summary line when it ends, e.g.
`Feedback loop (step 2) reached its max runtime of 20 s; continuing with the next step. 598 frames in 20.0 s (29.9 per
second); capture 2.1 ms + detection 0.4 ms per frame (slowest 9 ms), so it reacts about 3-36 ms after the screen
changes, plus the game's own delay; 41 input changes (…); marker found in 98% of frames, …`
It never writes one line per frame.

**Please send back the numbers marked 📏** (frames per second, capture ms, detection ms). They decide the default
"captures per second" for the next version.

---

## A. Editor basics

- [ ] **A1 Add the step.** Open a macro, click **Feedback loop** in the toolbox. A new step appears with the summary
  `Keep marker #FF0000 in zone 40–60 %: hold before, release after (left button; max 30 s)`. No errors, no warnings.
- [ ] **A2 Validation.** Set *Max runtime* to `0`. The Problems list shows "A feedback loop needs a max runtime…" and
  **Save is refused**. Set it back to `30000`. Try a few more: zone start `70` / end `30`, captures per second `0`,
  marker color `red`, all three rules = *Release*. Each one gives a clear error; fixing it removes the error.
- [ ] **A3 Warnings are only hints.** Set *Min time between changes* to `5`: a warning (not an error) appears and Save
  still works. Set it back to `50`.
- [ ] **A4 Save + reopen.** Fill in everything (including a *Done when* and a *Failed when* condition), save, restart
  SmartMacro, open the macro. All settings are still there.
- [ ] **A5 Export / import.** Export the macro (`.smartmacro`) and import it again: it loads. (The file says
  `"schemaVersion": 6` if you open it in Notepad.) An older SmartMacro build would refuse it as "made by a newer version".
- [ ] **A6 Old macros.** Open a few macros you made with Stages 1–3. They open, play and save as before (their files
  keep their old format number unless you add a Feedback loop).

## B. Measure loop speed 📏

Setup: open the test page, choose **Mode 1 – Moving marker**. Put the browser and SmartMacro side by side.

- [ ] **B1 Pick the bar.** In the Feedback loop step click **Pick bar area** and drag a box tightly around the dark
  bar (include the whole bar, not the page around it). The result box says the area and "direction: left ↔ right".
- [ ] **B2 Pick the marker color.** Click **Pick from screen** next to *Marker color* and click the red marker.
  The color becomes `#FF0000` (or very close – browsers sometimes shift colors a little; that's what tolerance is for).
- [ ] **B3 Measure.** Click **Measure loop speed**. SmartMacro hides for 3 s, then a picture of the bar opens with the
  marker boxed in green and the zone in blue, and the result box says frames per second, capture ms, detection ms,
  the slowest frame, how often the marker was found, and a suggested "captures per second".
  📏 Write down: area size, frames per second, capture ms, detection ms, slowest ms, suggested value.
- [ ] **B4 Bigger area.** Pick a much bigger area (half the screen) and measure again. 📏 Frames per second should drop and
  capture ms rise. (You also get a warning in the Problems list for areas over 250,000 pixels.) Pick the bar again.
- [ ] **B5 While the marker is hidden.** Scroll the page so the bar is out of the picked area and measure.
  The result says the marker was missing in (almost) 100 % of frames. Scroll back.

## C. Preview (no input is sent)

Setup: test page in Mode 1. Step settings: zone **A fixed part of the bar**, 40 to 60; *left of the zone* = **Hold down**,
*inside* = **Keep as it is**, *right of the zone* = **Release**.

- [ ] **C1 Live preview.** Click **Preview 10 s**. SmartMacro hides and a small window appears at the bottom right
  showing the bar (green box = marker, blue box = zone) and lines like
  `marker 23 % · zone 40–60 % · before the zone → would PRESS` / `Input: HELD · 30 fps …`.
  The test page's "Input:" text stays **released** the whole time (nothing is pressed).
- [ ] **C2 It follows the rules.** While the marker moves right through the zone it shows "would RELEASE" once it is
  right of the zone, and "would PRESS" once it's back left of the zone. Inside the zone nothing changes.
- [ ] **C3 Stop button.** Start the preview again and click **Stop preview** after 2–3 s: it stops early and the result
  says "Preview (stopped)". Closing the small window with its X does the same.
- [ ] **C4 Result text.** After the preview, the result box says how often it would have pressed / released, the
  frame rate and the reaction time ("reacts about X–Y ms after the screen changes").

## D. Real input on the test page (Mode 1)

Macro: `Move mouse` to a point **on the dark bar** (so the presses land on the page) → `Feedback loop` (settings from C,
*Max runtime* `15000`, *When the time is up* = Continue) → `Type text` is not needed.

- [ ] **D1 Hold / release.** Play. While the marker is left of the zone the page shows **Input: HELD**; right of the
  zone it shows **released**; inside the zone it keeps whatever it was. The *Presses* counter goes up about once per
  sweep, not many times per sweep.
- [ ] **D2 It ends on time.** After 15 s the loop ends, the page shows **released**, and the log has one summary line
  that says "reached its max runtime of 15 s; continuing with the next step". 📏 Write down its frames per second.
- [ ] **D3 Dead band.** Set *Dead band* to `0` and the marker speed to *slow*. Play again. Then set dead band `5`.
  With 5 the input never flickers when the marker sits near an edge (the Presses counter only goes up once per sweep).
- [ ] **D4 Key instead of mouse.** Set *Which input* = **A key**, Key = **Space**. Play: the page shows HELD / released
  the same way (the page treats Space like the mouse button).
- [ ] **D5 Tap.** Set *left of zone* = Release, *inside* = **Tap once when it gets there**, *right of zone* = Release.
  Play: the Presses counter goes up once each time the marker enters the zone (twice per back-and-forth), and the
  page never stays HELD.
- [ ] **D6 Minimum time between changes.** Set *Min time between changes* to `1000` and dead band `0`, marker speed
  *fast*. Play: changes happen at most once per second (the log line says how many were "delayed by the minimum time
  between changes").

## E. Catch game: moving zone + done / failed conditions (Mode 2)

Setup: test page **Mode 2 – Catch game**, Zone = **simple**, press **Restart** just before playing.
Step settings: zone **Found by its color**, pick the white zone (`#FFFFFF`); *left of the zone* = **Release**,
*inside* = **Keep as it is**, *right of the zone* = **Hold down**; dead band `2`; min time between changes `30`;
captures per second = the suggestion from B3; *Max runtime* `60000`.
*Done when*: Look for **Color** `#00C800` in an area around the status square (Pick area). *Failed when*: Color
`#0000C8` in the same area. Before the loop: `Move mouse` onto the bar.

- [ ] **E1 Preview first.** Click **Preview 10 s** while you play the page by hand (hold the mouse button over the page).
  The small window follows the white zone (blue box) and the marker, and says before / inside / after correctly.
- [ ] **E2 It plays the game.** Restart the page and play the macro. The white zone follows the red marker; the progress
  bar fills; the status square turns green and the loop ends right away with "finished: its "done when" condition is
  true". The mouse button is released at the end (page shows released).
  📏 How many seconds did it take? Did it win? Try 3 times.
- [ ] **E3 Failure.** Set the marker speed to *fast* and Zone = **heavy** (momentum). Play (and/or set *Captures per
  second* to `5`). It will often lose: when the square turns blue the macro **stops with an error** "failed: its
  "failed when" condition is true". Change *When it failed* to **Continue with the next step**, add a `Delay 1000` step
  after the loop, and check that the macro now finishes normally (the log says "continuing with the next step").
  This is the honest limit: a loop that only reacts to what it sees, a few frames late, does badly with momentum.
- [ ] **E4 Target lost.** While the loop runs, scroll the page so the bar leaves the picked area (or switch to another
  tab). With *Then* = **Release and keep looking**, the page/button is released, and when you scroll back it starts
  pressing again. With **Release and continue with the next step**, the loop ends ("lost its target") after
  *Target missing for* ms. With **Release and stop the macro** it stops with an error.

## F. Safety (do all of these)

- [ ] **F1 Esc.** During a loop that is holding the button (Mode 1, marker left of the zone), press **Esc**. Playback
  stops within a frame or two; the page shows **released**; the log line says "was stopped".
- [ ] **F2 Stop shortcut / button.** Same with Ctrl+Shift+F10 (or your Stop shortcut).
- [ ] **F3 Pause.** Press **F11 (Pause)** while it is HELD: the page shows **released** while paused. Wait 10 s and
  press F11 again: it is pressed again (if the marker is still left of the zone) and continues. The max runtime
  doesn't count the paused time (a 15 s loop paused for 10 s ends about 25 s after it started).
- [ ] **F4 Inside a Repeat.** `Repeat` 3 times → `Feedback loop` (max runtime 5000) → `Delay 1000` → `End Repeat`.
  Three summary lines in the log, the button is released between turns, Esc works in every turn.
- [ ] **F5 Close SmartMacro during a loop.** While it is HELD, close SmartMacro's window (confirm if asked). The page
  shows **released** afterwards.
- [ ] **F6 No CPU runaway.** During a 60 s loop at 30 captures per second, Task Manager shows SmartMacro using a
  modest share of the CPU (write it down 📏). The editor stays responsive after the run.

## G. A real game (optional, only where automation is allowed)

Only in a game whose rules allow macros (or a private / test place), with the game in **windowed or borderless**
mode (exclusive full-screen may capture as black). Nothing here is game-specific in SmartMacro: you set it up with
the same steps as on the test page.

- [ ] **G1 Measure in the game.** Pick the game's bar, pick the marker color, **Measure loop speed**. 📏 fps, capture ms,
  how often the marker was "unclear" (two similar patches) or missing. If it's often unclear, pick a tighter area
  or lower the tolerance.
- [ ] **G2 Preview in the game.** Preview 10 s while you play the minigame by hand: does it say before / inside /
  after correctly? (If not, the colors or the area need work – don't try real input yet.)
- [ ] **G3 Real run.** Only after G2 looks right. Write down whether the game reacted to the presses, whether it won,
  and the summary line. A game can ignore simulated input or react too late; that is not something SmartMacro can
  fix by itself.

## What to send back

- `artifacts\build.log` and `artifacts\test.log` from `tools\verify.cmd`.
- Which tests passed / failed (numbers), plus the 📏 numbers from B3, B4, D2, E2 and F6.
- For any failure: the summary line from the log and a screenshot of the step's settings.
