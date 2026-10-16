# SmartMacro

**Desktop automation for Windows: record, edit, play, and find text on screen.**

Owner & developer: **Riku** · Version 1.0.0

SmartMacro records your mouse and keyboard, lets you edit the result step by step, and plays it back.
It can also wait for text to appear on screen and click it, using the text recognition (OCR) built into Windows.
Everything stays on your PC. Nothing is uploaded.

---

## Requirements

- Windows 10 version 2004 (May 2020 Update) or newer, or Windows 11, 64-bit.
- Nothing else to install. The release package includes everything it needs.
- For the **Find text (OCR)** step, Windows needs an OCR language. Most PCs already have one for their display language. Check under **Settings → Text recognition** in SmartMacro.
  - If it's missing, go to Windows Settings → Time & language → Language & region. Open your language's options, add "Optical character recognition", then restart SmartMacro.

## Install / run

1. Unzip `SmartMacro-1.0.0-win-x64.zip` to any folder, for example `Documents\SmartMacro`.
2. Double-click **SmartMacro.exe**.
3. The first time, Windows SmartScreen may say "Windows protected your PC". The app isn't code-signed, because signing certificates are paid. Click **More info → Run anyway**.

Your macros and settings live in `%LOCALAPPDATA%\SmartMacro`, separate from the app folder. Updating means replacing the app folder; your macros stay.

## Quick start

1. **Record:** press **Record** (or Ctrl+Shift+F9), do your task, then press Ctrl+Shift+F9 again. A new macro opens in the editor.
2. **Edit:** select a step to change it on the right. Use the left panel to add steps. Reorder with the arrows (Alt+Up/Down) and delete with Delete.
3. **Play:** press **Play** (or Ctrl+Shift+F10).
   - In the editor header, set how many times to repeat (0 = until stopped), the speed, and a wait between repeats.
4. **Find text:** add a **Find text (OCR)** step. Type the text, optionally click **Pick area on screen**, press **Test now**, and choose what happens when it's found (for example, "Click it").

## Recording (one Record button for apps and games)

You don't have to pick a mode first. SmartMacro records every mouse event **twice on one timeline**: where the
cursor was (screen position) *and* how far the mouse moved (relative movement), plus the exact down/up time of every
key and button. When you stop, it looks at what the cursor was doing:

- **The cursor moved freely** (apps, menus, shop buttons) → normal editable steps: Click, Press, Move, Delay.
- **A game hid or locked the cursor while the mouse moved** (turning the camera, holding the right button to look
  around) → one **Recorded input** step that plays that part as relative movement, like a real mouse.
  Keys and buttons held across the change (for example holding W while turning) stay in that step so they're never cut.

Open a **Recorded input** step to see its parts. Each part says what was detected and why ("the cursor was hidden",
"the mouse moved but the cursor stayed in place"…). If SmartMacro guessed wrong, change the part to
**Cursor position** or **Relative movement** — no need to record again. Each part always plays the mouse one way only.

Tips for games:

1. Put your character in the **same spot facing the same way** before recording and before playing.
2. Keep the game's mouse sensitivity, window size and frame rate the same as when you recorded.
3. Use the record shortcut to stop (clicking SmartMacro's own Stop button isn't recorded, but clicking the taskbar is).

Even with a perfect recording, a game can play it back a little differently: frame rate, physics, network lag and game
updates all change results, and some games ignore simulated input. Small differences can add up over a long recording.
SmartMacro never tries to get around anti-cheat.

**Advanced:** Settings → Recording mode still offers the older **Desktop only** and **Game only** recorders as fallbacks.
Macros made with them keep working exactly as before.

## Find image and Find color (visual detection)

These steps look at the screen instead of using fixed positions, so a macro keeps working when a button moves.
Everything stays on your PC; no screenshots are uploaded.

- **Find image:** click **Capture image** and drag a box tightly around a button or icon. The step waits until that
  picture appears (or **disappears**) and can move to / click it. **Match accuracy** (90 % is a good start) says how
  similar it must be; **Ignore color** matches the shape only. It finds the picture only at the **same size**, so
  changing display scaling, game resolution or graphics quality may need a new capture.
- **Find color:** pick a color from the screen (**Pick from screen**), set a **tolerance** and a **small search area**.
  The biggest patch of that color counts as found. This is simple color matching, not object recognition: anything
  else with a similar color in the area also counts. **Track for 3 s** follows a moving colored marker in the area and
  reports its positions (0–100 % along the area) and how many frames per second were reached.
- **Find text** can now also **wait until the text disappears** (for example "Loading…").
- **Test now** checks a step without playing the macro and opens a picture with the matches marked
  (saved as `Logs\last-image-test.png` / `Logs\last-color-test.png`). The log says how long each capture and search took.
- A **Mouse click** step can click "what the last Find text / image / color step found".
- Games in exclusive full-screen mode may look black to screen capture; use windowed or borderless mode.

## If / Else and Repeat (conditions and loops)

The toolbox has a **Logic** section. Blocks are marked by pairs of steps, and the steps between them are indented:

- **If … Else … End If:** the If checks a **condition** — some text, an image or a color **is** (or **is NOT**) on
  screen, using the same settings and **Test now** as Find text / image / color. True → the steps up to Else run;
  false → the steps after Else run (Else is optional). "Keep checking for" lets the If wait for the condition to
  become true (0 = look once). That also covers "wait until visible, otherwise do something else".
- **Repeat … End Repeat:** a number of times, **while** a condition is true, or **until** it becomes true. The
  condition is checked before every turn. While / Until loops **must** have a safety limit (Max turns and/or Max
  time), and you choose whether reaching it stops the macro or just leaves the loop.
- Adding If or Repeat from the toolbox also adds its End. **Wrap in If / Wrap in Repeat** (above the step list) puts
  a block around the selected steps (Shift+click to select several). Deleting an If or Repeat removes its markers
  but keeps the steps inside.
- The Problems list shows blocks that don't match up (an End If without an If, blocks closed in the wrong order,
  more than 8 levels deep). Such macros can't be saved or played.
- Esc / Stop / Pause work inside loops and while a condition waits; held keys and buttons are always released.
- Macros with blocks are saved as file format 5 (older SmartMacro builds will say the file is from a newer version).
  Macros without blocks keep their old format. Conditions are plain settings, never scripts or code.

## Feedback loop (keep a moving marker in a zone)

Some minigames show a bar with a moving marker and a target zone, and you have to hold or release a key / mouse
button to keep the marker in the zone. The **Feedback loop** step does that kind of thing in real time:

1. **The bar and the marker:** **Pick bar area** (drag tightly around the bar), then **Pick from screen** for the
   marker color. The direction (left ↔ right or up ↕ down) is set from the shape of the area.
2. **The target zone:** either a fixed part of the bar (for example 40–60 %) or **found by its color** (a zone that
   moves – pick its color too).
3. **What to do** when the marker is left of / inside / right of the zone: **Hold down**, **Release**,
   **Keep as it is**, or **Tap once when it gets there**. A **dead band** stops flicker at the zone edges.
4. **Which input:** one mouse button (pressed where the cursor is – put a *Move mouse* step before the loop) or one key.
5. **Limits (required):** captures per second, minimum time between input changes, a **max runtime**, and what to do
   when the marker can't be seen for a while (release and keep looking / continue with the next step / stop).
6. **Stop when (optional):** a **done** condition and a **failed** condition – the same text / image / color
   conditions as If and Repeat.

Before using real input, use the two buttons at the bottom: **Measure loop speed** (how many frames per second your
PC manages for this area, and a suggested setting) and **Preview 10 s** (runs the loop for real but presses nothing;
a small window shows what it sees and what it would do). `docs\feedback-test-page.html` is a little offline test page
with a moving marker and a "catch game" to practise on before any game.

While a loop runs, Stop, Esc and Pause work every frame; the key / button is always released when it ends for any
reason. The log gets one summary line per loop (frames, frames per second, capture and detection time, input changes,
why it ended). Macros with a Feedback loop are saved as file format 6.

Honest limits: it reacts to what it sees, a few frames late (capture + detection + the game's own delay, usually
somewhere around 20–100 ms in total). That's fine for simple "keep the marker in the zone" bars; it handles zones with
momentum, very fast markers, flashing effects or look-alike colors much worse, and it can't win every minigame. It only
compares colors, never reads game memory, and doesn't get around anti-cheat.

## Safety

- **Esc** on your keyboard stops playback immediately.
- Ctrl+Shift+F10 also stops playback. You can change all shortcuts in **Settings → Keyboard shortcuts**.
- While recording or playing, a small banner at the top of the screen shows what SmartMacro is doing.
- Keys or mouse buttons held by a macro are always released when it stops, and while it's paused (they're pressed again when you resume).

## Library

- **Play / Edit / Duplicate / Delete** each macro. Deleted macros go to a Trash folder, so you can still recover them.
- **Export…** saves a macro as a `.smartmacro` file to share or back up. **Import** adds macros from files.
  - Imported files are checked first. They can only contain SmartMacro actions, never programs.

## Known limits

- Windows doesn't allow normal apps to control or record apps running **as administrator**. Nothing works on the lock screen or on UAC prompts.
- Some games with anti-cheat ignore simulated input. SmartMacro doesn't try to bypass this.
- Mouse positions are exact screen pixels. If you change monitors, resolution or scaling, recorded clicks may land in the wrong place. Use **Find text** steps for more robust macros.
- OCR reads normal horizontal text best. Very small, stylized or rotated text may not be found.
- Feedback loops react a few frames late and only compare colors; they can't handle every minigame (see above).
- Find image only finds the picture at the size it was captured, and Find color only compares colors. Neither one
  "understands" what is on screen, and neither can guarantee that a game will react to the clicks it sends.

## Troubleshooting

- **Settings → Copy diagnostics** copies version, Windows, screen and OCR info plus the recent log, for bug reports.
- Daily log files: `%LOCALAPPDATA%\SmartMacro\Logs`.
- **Find text → Test now** saves `Logs\last-ocr-test.png`, which shows exactly what was captured.
- **Find image / Find color → Test now** shows (and saves) the captured area with matches marked: green = the one a
  macro would use, orange = other matches. "Not found" results include the best similarity seen.
- **Clicks in a game's menus don't register:** check the log for lines starting with "Input check:". They say if the
  game window wasn't active (the click only switched to it) or if the game had locked the mouse.
- **The camera didn't turn (or the cursor jumped instead):** open the **Recorded input** step and check its parts. A
  camera part must say "Plays as: relative movement"; a menu part should say "cursor position". Change the part if not.
- After recording, the log says how many cursor and camera parts were found.
- **A Feedback loop doesn't react / reacts wrongly:** use **Preview 10 s** first. If it says "marker UNCLEAR" or
  "MISSING" a lot, pick a tighter bar area, pick the color again or change the tolerance. If the preview is right but
  the game doesn't react, the game may ignore simulated input or need the cursor over its window (add a Move mouse step).

---

© 2026 Riku
