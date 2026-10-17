# Next-gen QoL batch – manual tests (beginner checklist)

Do these after `tools\verify.cmd` is green. Tick each box; if something fails, write down the step number and
what you saw (a screenshot of the Properties panel or the Logs page helps a lot).

You need: SmartMacro, Notepad, and (for the game parts) Roblox. A second monitor is useful for A2–A4 but not required.

## A. "Where to look" (monitors and windows)

- [ ] **A1 – New steps use the main monitor.** Add a *Find image* step. In the Properties panel, card
  **2. Where to look** shows **Main Monitor (Primary Screen)**. The blue summary banner at the top says
  "Searches Main Monitor every 250 ms for up to 10 s…".
- [ ] **A2 – Specific monitor.** Choose **Specific Monitor…**. The list shows `Monitor 1 — Primary (…)` and your other
  monitors with their sizes. Pick Monitor 2, put a Notepad window with the word `OK` on monitor 2, make a *Find text*
  step for `OK` with **Specific Monitor → Monitor 2**, click **Test detection now** → it's found. Switch to
  **Main Monitor** → not found (it doesn't look at monitor 2 any more).
- [ ] **A3 – Refresh monitors.** (Optional) unplug / plug a monitor, click **Refresh monitors** → the list updates.
  A monitor that is gone shows "(not connected right now)".
- [ ] **A4 – App window.** Choose **Specific App Window…**, click **Refresh windows**, pick *Notepad* in the list (the
  title box fills in). Test detection finds `OK` inside Notepad. Move the Notepad window somewhere else and test again
  → still found (the window's current position is used).
- [ ] **A5 – Window not open.** Close Notepad, test again → "Can't test right now: The window "…" isn't open." Play a
  macro with that step → after the wait time it says the window isn't open.
- [ ] **A6 – Custom area.** Choose **Custom Area on Screen** → X / Y / Width / Height appear. **Pick area on screen**
  still works and switches the choice to Custom Area by itself.
- [ ] **A7 – Old macros.** Open a macro saved before this update that used "Search the whole screen" → it shows
  **All Monitors (Entire Desktop)** and plays exactly as before. One that used a picked area shows **Custom Area**.
- [ ] **A8 – Conditions.** In an *If* and a *Repeat While* condition the same "Where to look" card is there.

## B. Mouse movement and click timing

- [ ] **B1 – Glide.** Add *Move mouse* (default **Smooth Glide**, 120 ms), pick a far spot, play → the cursor visibly
  glides there instead of jumping.
- [ ] **B2 – Human curve.** Switch to **Human Curve**, set 600 ms, play → it travels on a slight arc, slow – fast – slow.
- [ ] **B3 – Instant.** Switch to **Instant** → it jumps (old behaviour).
- [ ] **B4 – Slider.** Roblox Settings (or any slider in Windows): *Mouse click* → Drag still works; a *Find image* with
  Smooth Glide on a button shows the button's hover effect before the click.
- [ ] **B5 – Click timing.** *Mouse click* → open **Advanced Tuning & Movement**: click hold 35, double-click gap 65,
  wait after click 120 for new steps. Double-click a desktop icon with the step → it opens.
- [ ] **B6 – Move cursor away.** Find image on the Fisch **SHAKE** button, *Click its center*, Advanced →
  **Move cursor away after click** on. Play a loop → after each click the cursor glides out of the way and the next
  SHAKE is found without the cursor covering it.

## C. Freeze-screen picker

- [ ] **C1 – Freeze immediately.** Find image → **Capture image** → the whole screen freezes (a moving video / game
  stands still) with the banner *"Screen Frozen — Drag to select or click a pixel | Press [Space] to unfreeze &
  capture a new frame | [Esc] Cancel"*. Drag a box → the captured picture is exactly what you saw frozen.
- [ ] **C2 – Countdown.** Set **Freeze: After 3s countdown**, click Capture, switch to Roblox within 3 s → a
  "Freezing the screen in 3… 2… 1…" banner appears (it doesn't take focus from the game), then the screen freezes.
- [ ] **C3 – Space.** In the frozen picker press **Space** → the overlay disappears, (after the countdown if one is set)
  a new frame is frozen.
- [ ] **C4 – Magnifier.** Move the cursor over the frozen screen → an 8× pixel grid follows the cursor with the centre
  pixel marked, its color swatch, `#RRGGBB   RGB(r, g, b)` and `X …, Y …`.
- [ ] **C5 – Pick color.** Find color → **Pick from screen** on a fast-moving marker (use `docs/feedback-test-page.html`)
  → the picked color is the one under the crosshair in the frozen picture.
- [ ] **C6 – Esc / right-click** cancel at any point, including during the countdown.
- [ ] **C7 – Two monitors (if you have them):** both monitors freeze, the box / pixel can be picked on either.

## D. Beginner-friendly Properties panel

- [ ] **D1 – Cards.** Find text / image / color show numbered cards **1. What to find**, **2. Where to look**,
  **3. What to do when found**, **4. Search timing**, then a closed **Advanced Tuning & Movement** section.
- [ ] **D2 – Summary banner** changes live when you change the action, timing or where to look.
- [ ] **D3 – Advanced** opens / closes with one click and contains match accuracy, the see-through game UI options
  (image), movement style, glide duration, click hold, wait after click, double-click gap, move away, click offsets.
- [ ] **D4 – Tooltips.** Hover labels and boxes → each has a plain-English explanation.
- [ ] **D5 – Window size.** Everything stays readable in a non-maximized window (text wraps, nothing cut off).

## E. Feedback loop guided setup

- [ ] **E1 – Cards.** Add a *Feedback loop* → Step 1 shows four cards (Horizontal Bar Follower, Vertical Bar Follower,
  Timing / Skill Check, Custom / Advanced). Selecting one highlights it.
- [ ] **E2 – Steps 2–4.** Pick bar area, pick target color, pick your bar color (with the freeze picker); the swatches
  update; the tolerance sliders change the numbers.
- [ ] **E3 – Rule preview.** The "What the loop will do" card explains it in sentences, e.g. *"While the target is to
  the RIGHT of your bar: holds the left mouse button down."* Switch to Vertical → it talks about ABOVE / BELOW.
- [ ] **E4 – Custom** shows every raw setting (like before).

## F. Live step highlight and the mini status pill

- [ ] **F1 – Highlight.** Untick **Minimize main window while playing** (top bar), play a macro with a Repeat loop → the
  running step has a green **▶ Running** badge and the list scrolls to it. The badge disappears when playback ends.
- [ ] **F2 – Pill.** With **Show mini status overlay while playing** ticked, play → a small pill in the top-right corner
  says e.g. `SmartMacro: Step 2/5 — Finding image "SHAKE" on the main monitor… (Esc to stop)`. You can click through
  it, it doesn't take focus from the game, and it closes when playback ends. (Tip: a step's *comment* is used as its
  name in the pill.)
- [ ] **F3 – Toggles are saved.** Change both toggles, restart SmartMacro → they're kept (also visible in Settings).

## G. Quick patterns

- [ ] **G1 – Auto-click loop.** Add step panel → **Quick patterns** → *Auto-Click Dynamic Button Loop* → a Repeat While +
  Find image + End Repeat are inserted; the Problems list asks for the two images. Capture the SHAKE button in both →
  no errors; play in Fisch.
- [ ] **G2 – Fishing template** inserts: Hold mouse 800 ms (Cast) → Delay → Shake loop → Feedback loop (Horizontal Bar
  Follower).
- [ ] **G3 – Shop template** inserts: Find image (click) → Scroll wheel → Mouse click.

📏 Please send back: whether the glide makes Roblox buttons react more reliably (B4 / B6), and the color the magnifier
showed for the Fisch fish / bar (C4).
